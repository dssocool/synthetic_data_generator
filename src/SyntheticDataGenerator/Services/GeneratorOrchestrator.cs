using System.Diagnostics;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class GeneratorOrchestrator
{
    private readonly string _connectionString;
    private readonly int _rowsPerTable;
    private readonly int? _seed;
    private readonly string? _schemaFilter;
    private readonly string _locale;
    private readonly string[] _tablesToInclude;
    private readonly string[] _tablesToExclude;

    public GeneratorOrchestrator(
        string connectionString,
        int rowsPerTable,
        int? seed,
        string? schemaFilter,
        string locale,
        string[] tablesToInclude,
        string[] tablesToExclude)
    {
        _connectionString = connectionString;
        _rowsPerTable = rowsPerTable;
        _seed = seed;
        _schemaFilter = schemaFilter;
        _locale = locale;
        _tablesToInclude = tablesToInclude;
        _tablesToExclude = tablesToExclude;
    }

    public async Task RunGeneratePlanAsync(string outputPath)
    {
        Console.WriteLine("=== Synthetic Data Generator - Generate Plan ===");
        Console.WriteLine($"Target: {MaskConnectionString(_connectionString)}");
        Console.WriteLine($"Output: {outputPath}");
        Console.WriteLine();

        var (sortedTables, graph) = await ReadAndSortSchemaAsync();
        if (sortedTables is null) return;

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sortedTables, graph!.SelfReferencingTables, _rowsPerTable, _seed, _locale);

        await planGen.WritePlanAsync(plan, outputPath);

        WarnUnsupportedColumns(sortedTables);

        Console.WriteLine($"Plan generated with {plan.Tables.Count} table(s):");
        foreach (var t in plan.Tables)
        {
            var genCols = t.Columns.Count(c => !c.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  {t.Order,3}. {t.FullName,-40} [{t.Columns.Count} cols, {genCols} generated, {t.RowCount} rows]");
        }
        Console.WriteLine();
        Console.WriteLine($"Plan written to: {Path.GetFullPath(outputPath)}");
        Console.WriteLine("Edit the plan file to customize generators, row counts, or table order, then run:");
        Console.WriteLine($"  dotnet run -- --execute-plan {outputPath}");
    }

    public async Task RunExecutePlanAsync(string planPath)
    {
        Console.WriteLine("=== Synthetic Data Generator - Execute Plan ===");
        Console.WriteLine($"Target: {MaskConnectionString(_connectionString)}");
        Console.WriteLine($"Plan: {planPath}");
        Console.WriteLine();

        if (!File.Exists(planPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR: Plan file not found: {planPath}");
            Console.ResetColor();
            return;
        }

        var plan = await PlanGenerator.ReadPlanAsync(planPath);
        var sortedTables = plan.Tables.OrderBy(t => t.Order).ToList();

        Console.WriteLine($"Executing plan with {sortedTables.Count} table(s):");
        foreach (var t in sortedTables)
        {
            var genCols = t.Columns.Count(c => !c.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  {t.Order,3}. {t.FullName,-40} [{t.Columns.Count} cols, {genCols} generated, {t.RowCount} rows]");
        }
        Console.WriteLine();

        var selfRefTables = new HashSet<string>(
            sortedTables
                .Where(t => t.Columns.Any(c =>
                    c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase)
                    && c.GeneratorArgs.TryGetValue("isSelfReferencing", out var sr)
                    && Helpers.IsTruthy(sr)))
                .Select(t => t.FullName));

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        valueGen.SetPlanBasePath(Path.GetDirectoryName(Path.GetFullPath(planPath))!);
        var inserter = new DataInserter(_connectionString, valueGen, selfRefTables);

        await InsertTablesAsync(sortedTables, async tp =>
        {
            var inserted = await inserter.InsertTableFromPlanAsync(tp);
            return (tp.FullName, inserted);
        });
    }

    public async Task RunDirectAsync()
    {
        Console.WriteLine("=== Synthetic Data Generator ===");
        Console.WriteLine($"Target: {MaskConnectionString(_connectionString)}");
        Console.WriteLine($"Rows per table: {_rowsPerTable}");
        Console.WriteLine($"Seed: {_seed?.ToString() ?? "(random)"}");
        if (!string.IsNullOrEmpty(_schemaFilter))
            Console.WriteLine($"Schema filter: {_schemaFilter}");
        Console.WriteLine();

        var (sortedTables, graph) = await ReadAndSortSchemaAsync();
        if (sortedTables is null) return;

        Console.WriteLine("Insertion order:");
        for (var i = 0; i < sortedTables.Count; i++)
        {
            var t = sortedTables[i];
            var selfRef = graph!.SelfReferencingTables.Contains(t.FullName) ? " (self-referencing)" : "";
            var fkCount = t.ForeignKeys.Count;
            Console.WriteLine($"  {i + 1,3}. {t.FullName,-40} " +
                              $"[{t.Columns.Count} cols, {fkCount} FKs{selfRef}]");
        }
        Console.WriteLine();

        WarnUnsupportedColumns(sortedTables);

        var valueGen = new ColumnValueGenerator(_seed, _locale);
        var inserter = new DataInserter(_connectionString, valueGen, graph!.SelfReferencingTables);

        await InsertTablesAsync(sortedTables, async table =>
        {
            var inserted = await inserter.InsertTableAsync(table, _rowsPerTable);
            return (table.FullName, inserted);
        });

        var planOutputPath = "plan.yaml";
        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sortedTables, graph!.SelfReferencingTables, _rowsPerTable, _seed, _locale);
        await planGen.WritePlanAsync(plan, planOutputPath);
        Console.WriteLine($"Plan saved to: {Path.GetFullPath(planOutputPath)}");
    }

    private static async Task InsertTablesAsync<T>(
        List<T> tables,
        Func<T, Task<(string FullName, int Inserted)>> insertFunc)
    {
        var totalRows = 0;
        var stopwatch = Stopwatch.StartNew();

        Console.WriteLine("Generating and inserting data...");
        foreach (var table in tables)
        {
            var tableStopwatch = Stopwatch.StartNew();
            try
            {
                var (fullName, inserted) = await insertFunc(table);
                tableStopwatch.Stop();
                totalRows += inserted;
                Console.WriteLine($"  {fullName,-40} {inserted,6} rows  ({tableStopwatch.ElapsedMilliseconds,5} ms)");
            }
            catch (DataGenerationException ex)
            {
                tableStopwatch.Stop();
                PrintDataGenerationError(ex.TableName, ex);
            }
            catch (Exception ex)
            {
                tableStopwatch.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAILED: {ex.Message}");
                Console.ResetColor();
            }
        }

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"Done. {totalRows} total rows inserted in {stopwatch.Elapsed.TotalSeconds:F1}s.");
    }

    private async Task<(List<TableInfo>? SortedTables, DependencyGraph? Graph)> ReadAndSortSchemaAsync()
    {
        Console.WriteLine("Reading database schema...");
        var schemaReader = new SchemaReader(_connectionString);
        var tables = await schemaReader.ReadSchemaAsync(
            string.IsNullOrWhiteSpace(_schemaFilter) ? null : _schemaFilter);

        if (_tablesToInclude.Length > 0)
        {
            var includeSet = new HashSet<string>(_tablesToInclude, StringComparer.OrdinalIgnoreCase);
            tables = tables.Where(t => includeSet.Contains(t.TableName) || includeSet.Contains(t.FullName)).ToList();
        }

        if (_tablesToExclude.Length > 0)
        {
            var excludeSet = new HashSet<string>(_tablesToExclude, StringComparer.OrdinalIgnoreCase);
            tables = tables.Where(t => !excludeSet.Contains(t.TableName) && !excludeSet.Contains(t.FullName)).ToList();
        }

        Console.WriteLine($"Found {tables.Count} table(s).");
        Console.WriteLine();

        if (tables.Count == 0)
        {
            Console.WriteLine("No tables found. Exiting.");
            return (null, null);
        }

        Console.WriteLine("Building dependency graph...");
        var graph = new DependencyGraph();
        graph.Build(tables);

        List<TableInfo> sortedTables;
        try
        {
            sortedTables = graph.GetTopologicalOrder();
        }
        catch (InvalidOperationException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.ResetColor();
            return (null, null);
        }

        return (sortedTables, graph);
    }

    internal static void PrintDataGenerationError(string tableName, DataGenerationException ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  {tableName,-40} FAILED at row {ex.RowIndex}");
        Console.ResetColor();

        if (ex.FailedColumn is { } col)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    Likely failed column: [{col.ColumnName}]");
            Console.WriteLine($"      SQL type:        {FormatSqlType(col)}");
            Console.WriteLine($"      Generator:       {col.Generator}");
            Console.WriteLine($"      Generated .NET:  {col.GeneratedValueType ?? "null"}");
            Console.WriteLine($"      Generated value: {col.GeneratedValuePreview ?? "NULL"}");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"    Error: {ex.InnerException?.Message ?? ex.Message}");
        Console.ResetColor();
    }

    internal static string FormatSqlType(ColumnFailureDetail col)
    {
        var type = col.SqlType;
        if (col.MaxLength > 0 &&
            type.Contains("char", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("binary", StringComparison.OrdinalIgnoreCase))
            return $"{type}({(col.MaxLength == -1 ? "MAX" : col.MaxLength.ToString())})";
        if (col.Precision > 0 &&
            (type.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("numeric", StringComparison.OrdinalIgnoreCase)))
            return $"{type}({col.Precision},{col.Scale})";
        return type;
    }

    internal static void WarnUnsupportedColumns(List<TableInfo> tables)
    {
        foreach (var table in tables)
        {
            var skipped = table.Columns
                .Where(c => !c.IsIdentity && !c.IsComputed && !c.IsRowVersion && !c.IsSequenceDefault && PlanGenerator.IsUnsupportedType(c))
                .ToList();

            foreach (var col in skipped)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Warning: [{table.FullName}].[{col.Name}] has unsupported type '{col.SqlType}' — column will be skipped.");
                Console.ResetColor();
            }
        }
    }

    internal static string MaskConnectionString(string cs)
    {
        var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var masked = parts.Select(p =>
        {
            var kv = p.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Contains("password", StringComparison.OrdinalIgnoreCase))
                return $"{kv[0].Trim()}=***";
            return p.Trim();
        });
        return string.Join("; ", masked);
    }
}
