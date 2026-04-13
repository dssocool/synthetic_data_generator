using System.Diagnostics;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class GeneratorOrchestrator
{
    private readonly string _connectionString;
    private readonly ScopeConfig _scope;

    public GeneratorOrchestrator(string connectionString, ScopeConfig scope)
    {
        _connectionString = connectionString;
        _scope = scope;
    }

    public async Task RunGeneratePlanAsync(string outputPath, string mode = "bootstrap")
    {
        Console.WriteLine($"=== Synthetic Data Generator - Generate Plan ({mode}) ===");
        Console.WriteLine($"Target: {MaskConnectionString(_connectionString)}");
        Console.WriteLine($"Output: {outputPath}");
        Console.WriteLine();

        var columnScope = _scope.BuildColumnScope();
        var (sortedTables, graph) = await ReadAndScopeSchemaAsync(mode, columnScope);
        if (sortedTables is null) return;

        if (mode.Equals("update", StringComparison.OrdinalIgnoreCase))
            ValidateUpdateScope(columnScope!, sortedTables, await ReadAllTablesAsync());

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            sortedTables, graph!.SelfReferencingTables, _scope.RowsPerTable,
            _scope.Seed, _scope.Locale, mode, columnScope);

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
        var planMode = string.IsNullOrWhiteSpace(plan.Mode) ? "bootstrap" : plan.Mode;
        Console.WriteLine($"Plan mode: {planMode}");

        if (planMode.Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteUpdatePlanAsync(plan, planPath);
            return;
        }

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

    public async Task RunDirectAsync(string mode = "bootstrap")
    {
        Console.WriteLine($"=== Synthetic Data Generator - Direct ({mode}) ===");
        Console.WriteLine($"Target: {MaskConnectionString(_connectionString)}");
        Console.WriteLine($"Rows per table: {_scope.RowsPerTable}");
        Console.WriteLine($"Seed: {_scope.Seed?.ToString() ?? "(random)"}");
        if (!string.IsNullOrEmpty(_scope.SchemaFilter))
            Console.WriteLine($"Schema filter: {_scope.SchemaFilter}");
        Console.WriteLine();

        var columnScope = _scope.BuildColumnScope();
        var isUpdate = mode.Equals("update", StringComparison.OrdinalIgnoreCase);

        var (sortedTables, graph) = await ReadAndScopeSchemaAsync(mode, columnScope);
        if (sortedTables is null) return;

        if (isUpdate)
        {
            ValidateUpdateScope(columnScope!, sortedTables, await ReadAllTablesAsync());
            await RunUpdateDirectInternalAsync(sortedTables, columnScope!);
            return;
        }

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

        var valueGen = new ColumnValueGenerator(_scope.Seed, _scope.Locale);
        var inserter = new DataInserter(_connectionString, valueGen, graph!.SelfReferencingTables);

        await InsertTablesAsync(sortedTables, async table =>
        {
            var inserted = await inserter.InsertTableAsync(table, _scope.RowsPerTable);
            return (table.FullName, inserted);
        });

        var planOutputPath = "plan.yaml";
        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            sortedTables, graph!.SelfReferencingTables, _scope.RowsPerTable,
            _scope.Seed, _scope.Locale, mode, columnScope);
        await planGen.WritePlanAsync(plan, planOutputPath);
        Console.WriteLine($"Plan saved to: {Path.GetFullPath(planOutputPath)}");
    }

    private async Task RunUpdateDirectInternalAsync(
        List<TableInfo> sortedTables,
        Dictionary<string, HashSet<string>> columnScope)
    {
        var valueGen = new ColumnValueGenerator(_scope.Seed, _scope.Locale);
        var inserter = new DataInserter(_connectionString, valueGen, new HashSet<string>());

        await UpdateTablesAsync(sortedTables, async table =>
        {
            if (!columnScope.TryGetValue(table.FullName, out var scopedCols))
                return (table.FullName, 0);

            var columnsToUpdate = table.Columns
                .Where(c => scopedCols.Contains(c.Name))
                .ToList();

            var fkGroups = BuildUpdateFkGroups(table, columnsToUpdate, columnScope);

            var updated = await inserter.UpdateTableAsync(
                table, columnsToUpdate, fkGroups,
                col => valueGen.Generate((ColumnInfo)col) ?? DBNull.Value);
            return (table.FullName, updated);
        });

        var planOutputPath = "plan.yaml";
        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            sortedTables, new HashSet<string>(), _scope.RowsPerTable,
            _scope.Seed, _scope.Locale, "update", columnScope);
        await planGen.WritePlanAsync(plan, planOutputPath);
        Console.WriteLine($"Plan saved to: {Path.GetFullPath(planOutputPath)}");
    }

    private async Task ExecuteUpdatePlanAsync(GenerationPlan plan, string planPath)
    {
        var tables = plan.Tables.OrderBy(t => t.Order).ToList();

        Console.WriteLine($"Executing update plan with {tables.Count} table(s):");
        foreach (var t in tables)
        {
            var genCols = t.Columns.Count(c => !c.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  {t.Order,3}. {t.FullName,-40} [{genCols} cols to update]");
        }
        Console.WriteLine();

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        valueGen.SetPlanBasePath(Path.GetDirectoryName(Path.GetFullPath(planPath))!);
        var inserter = new DataInserter(_connectionString, valueGen, new HashSet<string>());

        await UpdateTablesAsync(tables, async tablePlan =>
        {
            var updated = await inserter.UpdateTableFromPlanAsync(
                tablePlan,
                col => valueGen.GenerateFromPlan((ColumnPlan)col) ?? DBNull.Value);
            return (tablePlan.FullName, updated);
        });
    }

    private async Task<List<TableInfo>> ReadAllTablesAsync()
    {
        var schemaReader = new SchemaReader(_connectionString);
        return await schemaReader.ReadSchemaAsync(
            string.IsNullOrWhiteSpace(_scope.SchemaFilter) ? null : _scope.SchemaFilter);
    }

    private async Task<(List<TableInfo>? SortedTables, DependencyGraph? Graph)> ReadAndScopeSchemaAsync(
        string mode,
        Dictionary<string, HashSet<string>>? columnScope)
    {
        Console.WriteLine("Reading database schema...");
        var tables = await ReadAllTablesAsync();

        if (_scope.TablesToInclude.Length > 0)
        {
            ValidateScope(tables, _scope.TablesToInclude);
            var includeSet = _scope.GetIncludeTableNames();
            tables = tables.Where(t => includeSet.Contains(t.TableName) || includeSet.Contains(t.FullName)).ToList();
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
        graph.Build(tables, columnScope);

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

    internal static void ValidateScope(
        List<TableInfo> allTables,
        TableScope[] tablesToInclude)
    {
        var tableByName = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
        var tableByFullName = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTables)
        {
            tableByName.TryAdd(t.TableName, t);
            tableByFullName.TryAdd(t.FullName, t);
        }

        var missingTables = new List<string>();
        var missingColumns = new List<(string Table, string Column)>();

        foreach (var entry in tablesToInclude)
        {
            if (!tableByFullName.TryGetValue(entry.Table, out var matched)
                && !tableByName.TryGetValue(entry.Table, out matched))
            {
                missingTables.Add(entry.Table);
                continue;
            }

            if (entry.Columns is not { Count: > 0 })
                continue;

            var dbColumns = new HashSet<string>(
                matched.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var col in entry.Columns)
            {
                if (!dbColumns.Contains(col))
                    missingColumns.Add((matched.FullName, col));
            }
        }

        if (missingTables.Count == 0 && missingColumns.Count == 0)
            return;

        foreach (var table in missingTables)
            PrintFatal($"Table [{table}] does not exist in the database.");

        foreach (var (table, column) in missingColumns)
            PrintFatal($"Column [{column}] does not exist in table [{table}].");

        var parts = new List<string>();
        if (missingTables.Count > 0)
            parts.Add($"{missingTables.Count} table(s) not found: {string.Join(", ", missingTables)}");
        if (missingColumns.Count > 0)
            parts.Add($"{missingColumns.Count} column(s) not found: {string.Join(", ", missingColumns.Select(c => $"[{c.Table}].[{c.Column}]"))}");

        throw new InvalidOperationException(
            $"Scope validation failed — {string.Join("; ", parts)}.");
    }

    internal static void ValidateUpdateScope(
        Dictionary<string, HashSet<string>> columnScope,
        List<TableInfo> scopeTables,
        List<TableInfo> allTables)
    {
        foreach (var table in scopeTables)
        {
            if (table.PrimaryKeyColumns.Count == 0)
            {
                PrintFatal($"Table [{table.FullName}] has no primary key. Update mode requires a primary key.");
                throw new InvalidOperationException(
                    $"Table [{table.FullName}] has no primary key.");
            }

            if (!columnScope.TryGetValue(table.FullName, out var columnNames))
                continue;

            var tableColumnNames = new HashSet<string>(
                table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var pkSet = new HashSet<string>(
                table.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);

            foreach (var colName in columnNames)
            {
                if (!tableColumnNames.Contains(colName))
                {
                    PrintFatal($"Column [{colName}] not found in table [{table.FullName}].");
                    throw new InvalidOperationException(
                        $"Column [{colName}] not found in table [{table.FullName}].");
                }

                if (pkSet.Contains(colName))
                {
                    PrintFatal(
                        $"Column [{table.FullName}].[{colName}] is a primary key column and cannot be updated.");
                    throw new InvalidOperationException(
                        $"Column [{table.FullName}].[{colName}] is a primary key column and cannot be updated.");
                }
            }
        }

        ValidateUpdateForeignKeys(columnScope, scopeTables, allTables);
    }

    internal static void ValidateUpdateForeignKeys(
        Dictionary<string, HashSet<string>> columnScope,
        List<TableInfo> scopeTables,
        List<TableInfo> allTables)
    {
        foreach (var table in scopeTables)
        {
            if (!columnScope.TryGetValue(table.FullName, out var userCols))
                continue;

            foreach (var fk in table.ForeignKeys)
            {
                if (!userCols.Contains(fk.ParentColumn))
                    continue;

                if (!columnScope.TryGetValue(fk.FullReferencedTableName, out var refCols)
                    || !refCols.Contains(fk.ReferencedColumn))
                {
                    PrintFatal(
                        $"Column [{table.FullName}].[{fk.ParentColumn}] has a foreign key reference " +
                        $"to [{fk.FullReferencedTableName}].[{fk.ReferencedColumn}], " +
                        $"but [{fk.FullReferencedTableName}].[{fk.ReferencedColumn}] is not in the update columns list. " +
                        $"Both sides of a FK relationship must be included.");
                    throw new InvalidOperationException(
                        $"FK validation failed: [{table.FullName}].[{fk.ParentColumn}] references " +
                        $"[{fk.FullReferencedTableName}].[{fk.ReferencedColumn}] which is not in the update list.");
                }
            }
        }

        foreach (var table in allTables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (!columnScope.TryGetValue(fk.FullReferencedTableName, out var refUserCols))
                    continue;
                if (!refUserCols.Contains(fk.ReferencedColumn))
                    continue;

                if (!columnScope.TryGetValue(fk.FullParentTableName, out var parentUserCols)
                    || !parentUserCols.Contains(fk.ParentColumn))
                {
                    PrintFatal(
                        $"Column [{fk.FullReferencedTableName}].[{fk.ReferencedColumn}] is referenced by " +
                        $"foreign key from [{fk.FullParentTableName}].[{fk.ParentColumn}], " +
                        $"but [{fk.FullParentTableName}].[{fk.ParentColumn}] is not in the update columns list. " +
                        $"Both sides of a FK relationship must be included.");
                    throw new InvalidOperationException(
                        $"FK validation failed: [{fk.FullReferencedTableName}].[{fk.ReferencedColumn}] is referenced by " +
                        $"[{fk.FullParentTableName}].[{fk.ParentColumn}] which is not in the update list.");
                }
            }
        }
    }

    internal static List<DataInserter.UpdateFkGroup> BuildUpdateFkGroups(
        TableInfo table,
        List<ColumnInfo> columnsToUpdate,
        Dictionary<string, HashSet<string>> columnScope)
    {
        var groups = new List<DataInserter.UpdateFkGroup>();
        var updateColNames = new HashSet<string>(
            columnsToUpdate.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var fk in table.ForeignKeys)
        {
            if (fk.IsSelfReferencing) continue;
            if (!updateColNames.Contains(fk.ParentColumn)) continue;
            if (!columnScope.ContainsKey(fk.FullReferencedTableName)) continue;

            groups.Add(new DataInserter.UpdateFkGroup(
                fk.FullReferencedTableName, fk.ParentColumn, fk.ReferencedColumn));
        }

        return groups;
    }

    private static void PrintFatal(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  FATAL: {message}");
        Console.ResetColor();
    }

    private static async Task UpdateTablesAsync<T>(
        List<T> tables,
        Func<T, Task<(string FullName, int Updated)>> updateFunc)
    {
        var totalRows = 0;
        var stopwatch = Stopwatch.StartNew();

        Console.WriteLine("Generating data and updating...");
        foreach (var table in tables)
        {
            var tableStopwatch = Stopwatch.StartNew();
            try
            {
                var (fullName, updated) = await updateFunc(table);
                tableStopwatch.Stop();
                totalRows += updated;
                Console.WriteLine($"  {fullName,-40} {updated,6} rows  ({tableStopwatch.ElapsedMilliseconds,5} ms)");
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
        Console.WriteLine($"Done. {totalRows} total rows updated in {stopwatch.Elapsed.TotalSeconds:F1}s.");
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
