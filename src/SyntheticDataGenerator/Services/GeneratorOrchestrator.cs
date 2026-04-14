using System.Diagnostics;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class GeneratorOrchestrator
{
    private readonly string _connectionString;
    private readonly ScopeConfig _scope;
    private readonly IDataGenerationPlanner _planner;
    private readonly IDataGenerationExecutor _executor;

    public GeneratorOrchestrator(
        string connectionString,
        ScopeConfig scope,
        IDataGenerationPlanner planner,
        IDataGenerationExecutor executor)
    {
        _connectionString = connectionString;
        _scope = scope;
        _planner = planner;
        _executor = executor;
    }

    public async Task RunGeneratePlanAsync(string outputPath, string mode = "bootstrap")
    {
        Console.WriteLine($"=== Synthetic Data Generator - Generate Plan ({mode}) ===");
        Console.WriteLine($"Target: {MaskConnectionString(_connectionString)}");
        Console.WriteLine($"Output: {outputPath}");
        Console.WriteLine();

        var validateResult = await _planner.ValidateScopeAsync(
            new ValidateScopeCommand(_connectionString, _scope, mode), CancellationToken.None);

        if (!validateResult.IsValid)
        {
            PrintErrors(validateResult.Errors);
            return;
        }

        WarnUnsupportedColumns(validateResult.ScopedTables);
        WarnExternalDependencies(validateResult.ExternalDependencies);

        var planResult = await _planner.GeneratePlanAsync(
            new GeneratePlanCommand(validateResult, _scope, outputPath, mode), CancellationToken.None);

        Console.WriteLine($"Plan generated with {planResult.Plan.Tables.Count} table(s):");
        foreach (var t in planResult.Plan.Tables)
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
        var isUpdate = planMode.Equals("update", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"Plan mode: {planMode}");

        var sortedTables = plan.Tables.OrderBy(t => t.Order).ToList();
        Console.WriteLine($"Executing plan with {sortedTables.Count} table(s):");
        foreach (var t in sortedTables)
        {
            var genCols = t.Columns.Count(c => !c.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase));
            if (isUpdate)
                Console.WriteLine($"  {t.Order,3}. {t.FullName,-40} [{genCols} cols to update]");
            else
                Console.WriteLine($"  {t.Order,3}. {t.FullName,-40} [{t.Columns.Count} cols, {genCols} generated, {t.RowCount} rows]");
        }
        Console.WriteLine();

        WarnExternalDependencies(plan.ExternalDependencies);

        var stopwatch = Stopwatch.StartNew();
        var result = await _executor.ExecutePlanAsync(
            new ExecutePlanCommand(plan, _connectionString,
                Path.GetDirectoryName(Path.GetFullPath(planPath))),
            CancellationToken.None);
        stopwatch.Stop();

        PrintExecutionResult(result, isUpdate, stopwatch.Elapsed);
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

        var isUpdate = mode.Equals("update", StringComparison.OrdinalIgnoreCase);

        var validateResult = await _planner.ValidateScopeAsync(
            new ValidateScopeCommand(_connectionString, _scope, mode), CancellationToken.None);

        if (!validateResult.IsValid)
        {
            PrintErrors(validateResult.Errors);
            return;
        }

        if (!isUpdate)
        {
            Console.WriteLine("Insertion order:");
            var graph = validateResult.Graph!;
            for (var i = 0; i < validateResult.ScopedTables.Count; i++)
            {
                var t = validateResult.ScopedTables[i];
                var selfRef = graph.SelfReferencingTables.Contains(t.FullName) ? " (self-referencing)" : "";
                var fkCount = t.ForeignKeys.Count;
                Console.WriteLine($"  {i + 1,3}. {t.FullName,-40} " +
                                  $"[{t.Columns.Count} cols, {fkCount} FKs{selfRef}]");
            }
            Console.WriteLine();
        }

        WarnUnsupportedColumns(validateResult.ScopedTables);
        WarnExternalDependencies(validateResult.ExternalDependencies);

        var planOutputPath = "plan.yaml";
        var planResult = await _planner.GeneratePlanAsync(
            new GeneratePlanCommand(validateResult, _scope, planOutputPath, mode), CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine(isUpdate ? "Generating data and updating..." : "Generating and inserting data...");

        var execResult = await _executor.ExecutePlanAsync(
            new ExecutePlanCommand(planResult.Plan, _connectionString, null),
            CancellationToken.None);
        stopwatch.Stop();

        PrintExecutionResult(execResult, isUpdate, stopwatch.Elapsed);
        Console.WriteLine($"Plan saved to: {Path.GetFullPath(planOutputPath)}");
    }

    private static void PrintExecutionResult(ExecutePlanResult result, bool isUpdate, TimeSpan elapsed)
    {
        foreach (var detail in result.Tables)
        {
            if (detail.Success)
            {
                Console.WriteLine($"  {detail.TableName,-40} {detail.RowsAffected,6} rows");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  {detail.TableName,-40} FAILED: {detail.ErrorMessage}");
                Console.ResetColor();
            }
        }

        Console.WriteLine();
        var verb = isUpdate ? "updated" : "inserted";
        Console.WriteLine($"Done. {result.TotalRowsAffected} total rows {verb} in {elapsed.TotalSeconds:F1}s.");
    }

    private static void PrintErrors(List<string> errors)
    {
        foreach (var error in errors)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  FATAL: {error}");
            Console.ResetColor();
        }
    }

    internal static void WarnExternalDependencies(List<ExternalDependency>? deps)
    {
        if (deps is not { Count: > 0 })
            return;

        foreach (var dep in deps)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            if (dep.Direction.Equals("outbound", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"  Warning: [{dep.ScopedTable}].[{dep.ScopedColumn}] -> " +
                    $"[{dep.ExternalTable}].[{dep.ExternalColumn}] " +
                    $"(outbound: FK references table outside scope)");
            }
            else
            {
                Console.WriteLine(
                    $"  Warning: [{dep.ExternalTable}].[{dep.ExternalColumn}] -> " +
                    $"[{dep.ScopedTable}].[{dep.ScopedColumn}] " +
                    $"(inbound: external table references scoped table)");
            }
            Console.ResetColor();
        }
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
}
