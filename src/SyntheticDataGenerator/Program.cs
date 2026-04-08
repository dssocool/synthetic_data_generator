using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddYamlFile("appsettings.yaml", optional: false)
    .Build();

var baseConnectionString = config["ConnectionString"]
    ?? throw new InvalidOperationException("ConnectionString is required in appsettings.yaml");

var databaseName = config["DatabaseName"];
var connectionString = string.IsNullOrWhiteSpace(databaseName)
    ? baseConnectionString
    : new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(baseConnectionString)
        { InitialCatalog = databaseName }.ConnectionString;

var rowsPerTable = int.TryParse(config["RowsPerTable"], out var r) ? r : 100;
var seed = int.TryParse(config["Seed"], out var s) ? s : (int?)null;
var schemaFilter = config["Schema"];
var locale = config["Locale"] ?? "en";
var tablesToInclude = config.GetSection("TablesToInclude").Get<string[]>() ?? [];
var tablesToExclude = config.GetSection("TablesToExclude").Get<string[]>() ?? [];

var mode = ParseMode(args);

switch (mode)
{
    case ("generate-plan", var outputPath):
        await RunGeneratePlan(outputPath ?? "plan.yaml");
        break;
    case ("execute-plan", var planPath):
        await RunExecutePlan(planPath ?? throw new InvalidOperationException(
            "Usage: --execute-plan <plan-file-path>"));
        break;
    default:
        await RunDirect();
        break;
}

return;

async Task RunGeneratePlan(string outputPath)
{
    Console.WriteLine("=== Synthetic Data Generator - Generate Plan ===");
    Console.WriteLine($"Target: {MaskConnectionString(connectionString)}");
    Console.WriteLine($"Output: {outputPath}");
    Console.WriteLine();

    var (sortedTables, graph) = await ReadAndSortSchema();
    if (sortedTables is null) return;

    var planGen = new PlanGenerator();
    var plan = planGen.Generate(sortedTables, graph!.SelfReferencingTables, rowsPerTable, seed, locale);

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

async Task RunExecutePlan(string planPath)
{
    Console.WriteLine("=== Synthetic Data Generator - Execute Plan ===");
    Console.WriteLine($"Target: {MaskConnectionString(connectionString)}");
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
                && IsTruthy(sr)))
            .Select(t => t.FullName));

    var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
    valueGen.SetPlanBasePath(Path.GetDirectoryName(Path.GetFullPath(planPath))!);
    var inserter = new DataInserter(connectionString, valueGen, selfRefTables);
    var totalRows = 0;
    var stopwatch = Stopwatch.StartNew();

    Console.WriteLine("Generating and inserting data...");
    foreach (var tablePlan in sortedTables)
    {
        var tableStopwatch = Stopwatch.StartNew();
        try
        {
            var inserted = await inserter.InsertTableFromPlanAsync(tablePlan);
            tableStopwatch.Stop();
            totalRows += inserted;
            Console.WriteLine($"  {tablePlan.FullName,-40} {inserted,6} rows  ({tableStopwatch.ElapsedMilliseconds,5} ms)");
        }
        catch (DataGenerationException ex)
        {
            tableStopwatch.Stop();
            PrintDataGenerationError(tablePlan.FullName, ex);
        }
        catch (Exception ex)
        {
            tableStopwatch.Stop();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  {tablePlan.FullName,-40} FAILED: {ex.Message}");
            Console.ResetColor();
        }
    }

    stopwatch.Stop();
    Console.WriteLine();
    Console.WriteLine($"Done. {totalRows} total rows inserted in {stopwatch.Elapsed.TotalSeconds:F1}s.");
}

async Task RunDirect()
{
    Console.WriteLine("=== Synthetic Data Generator ===");
    Console.WriteLine($"Target: {MaskConnectionString(connectionString)}");
    Console.WriteLine($"Rows per table: {rowsPerTable}");
    Console.WriteLine($"Seed: {seed?.ToString() ?? "(random)"}");
    if (!string.IsNullOrEmpty(schemaFilter))
        Console.WriteLine($"Schema filter: {schemaFilter}");
    Console.WriteLine();

    var (sortedTables, graph) = await ReadAndSortSchema();
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

    Console.WriteLine("Generating and inserting data...");
    var valueGen = new ColumnValueGenerator(seed, locale);
    var inserter = new DataInserter(connectionString, valueGen, graph!.SelfReferencingTables);
    var totalRows = 0;
    var stopwatch = Stopwatch.StartNew();

    foreach (var table in sortedTables)
    {
        var tableStopwatch = Stopwatch.StartNew();
        try
        {
            var inserted = await inserter.InsertTableAsync(table, rowsPerTable);
            tableStopwatch.Stop();
            totalRows += inserted;
            Console.WriteLine($"  {table.FullName,-40} {inserted,6} rows  ({tableStopwatch.ElapsedMilliseconds,5} ms)");
        }
        catch (DataGenerationException ex)
        {
            tableStopwatch.Stop();
            PrintDataGenerationError(table.FullName, ex);
        }
        catch (Exception ex)
        {
            tableStopwatch.Stop();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  {table.FullName,-40} FAILED: {ex.Message}");
            Console.ResetColor();
        }
    }

    stopwatch.Stop();
    Console.WriteLine();
    Console.WriteLine($"Done. {totalRows} total rows inserted in {stopwatch.Elapsed.TotalSeconds:F1}s.");

    var planOutputPath = "plan.yaml";
    var planGen = new PlanGenerator();
    var plan = planGen.Generate(sortedTables, graph!.SelfReferencingTables, rowsPerTable, seed, locale);
    await planGen.WritePlanAsync(plan, planOutputPath);
    Console.WriteLine($"Plan saved to: {Path.GetFullPath(planOutputPath)}");
}

async Task<(List<TableInfo>? SortedTables, DependencyGraph? Graph)> ReadAndSortSchema()
{
    Console.WriteLine("Reading database schema...");
    var schemaReader = new SchemaReader(connectionString);
    var tables = await schemaReader.ReadSchemaAsync(
        string.IsNullOrWhiteSpace(schemaFilter) ? null : schemaFilter);

    if (tablesToInclude.Length > 0)
    {
        var includeSet = new HashSet<string>(tablesToInclude, StringComparer.OrdinalIgnoreCase);
        tables = tables.Where(t => includeSet.Contains(t.TableName) || includeSet.Contains(t.FullName)).ToList();
    }

    if (tablesToExclude.Length > 0)
    {
        var excludeSet = new HashSet<string>(tablesToExclude, StringComparer.OrdinalIgnoreCase);
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

static (string Mode, string? Arg) ParseMode(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--generate-plan":
                return ("generate-plan", i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null);
            case "--execute-plan":
                return ("execute-plan", i + 1 < args.Length ? args[i + 1] : null);
        }
    }
    return ("direct", null);
}

static bool IsTruthy(object? value)
{
    if (value is bool b) return b;
    if (value is string str) return str.Equals("true", StringComparison.OrdinalIgnoreCase);
    return false;
}

static void PrintDataGenerationError(string tableName, DataGenerationException ex)
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

static string FormatSqlType(ColumnFailureDetail col)
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

static void WarnUnsupportedColumns(List<TableInfo> tables)
{
    foreach (var table in tables)
    {
        var skipped = table.Columns
            .Where(c => !c.IsIdentity && !c.IsComputed && !c.IsRowVersion && PlanGenerator.IsUnsupportedType(c))
            .ToList();

        foreach (var col in skipped)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: [{table.FullName}].[{col.Name}] has unsupported type '{col.SqlType}' — column will be skipped.");
            Console.ResetColor();
        }
    }
}

static string MaskConnectionString(string cs)
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
