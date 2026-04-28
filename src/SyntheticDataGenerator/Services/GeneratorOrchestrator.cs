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

    public async Task RunGeneratePlanAsync(string outputPath, string mode = "insert")
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
        WarnExternalCustomDependencyRoots(validateResult.CustomDependencies, _scope.CustomDependencyBufferSize);

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
        var planMode = string.IsNullOrWhiteSpace(plan.Mode) ? "insert" : plan.Mode;
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
        WarnExternalCustomDependencyRoots(plan.CustomDependencies, _scope.CustomDependencyBufferSize);

        var tableCount = sortedTables.Count;
        var completed = 0;

        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine(isUpdate ? "Generating data and updating..." : "Generating and inserting data...");

        var result = await _executor.ExecutePlanAsync(
            new ExecutePlanCommand(plan, _connectionString,
                Path.GetDirectoryName(Path.GetFullPath(planPath)),
                _scope.CustomDependencyBufferSize,
                _scope.MaxParallelTables),
            CancellationToken.None,
            detail => PrintTableProgress(detail, ++completed, tableCount));
        stopwatch.Stop();

        PrintExecutionSummary(result, isUpdate, stopwatch.Elapsed);
    }

    public async Task RunDirectAsync(string mode = "insert")
    {
        Console.WriteLine($"=== Synthetic Data Generator - Direct ({mode}) ===");
        Console.WriteLine($"Target: {MaskConnectionString(_connectionString)}");
        Console.WriteLine($"Rows per table: {_scope.RowsPerTable}");
        Console.WriteLine($"Seed: {_scope.Seed?.ToString() ?? "(random)"}");
        if (_scope.SchemaFilter is { Length: > 0 })
            Console.WriteLine($"Schema filter: {string.Join(", ", _scope.SchemaFilter)}");
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
            var selfRefTables = validateResult.SelfReferencingTables ?? (IReadOnlySet<string>)new HashSet<string>();
            for (var i = 0; i < validateResult.ScopedTables.Count; i++)
            {
                var t = validateResult.ScopedTables[i];
                var selfRef = selfRefTables.Contains(t.FullName) ? " (self-referencing)" : "";
                var fkCount = t.ForeignKeys.Count;
                Console.WriteLine($"  {i + 1,3}. {t.FullName,-40} " +
                                  $"[{t.Columns.Count} cols, {fkCount} FKs{selfRef}]");
            }
            Console.WriteLine();
        }

        WarnUnsupportedColumns(validateResult.ScopedTables);
        WarnExternalDependencies(validateResult.ExternalDependencies);
        WarnExternalCustomDependencyRoots(validateResult.CustomDependencies, _scope.CustomDependencyBufferSize);

        var planOutputPath = "plan.yaml";
        var planResult = await _planner.GeneratePlanAsync(
            new GeneratePlanCommand(validateResult, _scope, planOutputPath, mode), CancellationToken.None);

        var sortedTables = planResult.Plan.Tables.OrderBy(t => t.Order).ToList();
        var tableCount = sortedTables.Count;
        var completed = 0;

        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine(isUpdate ? "Generating data and updating..." : "Generating and inserting data...");

        var execResult = await _executor.ExecutePlanAsync(
            new ExecutePlanCommand(planResult.Plan, _connectionString, null,
                _scope.CustomDependencyBufferSize,
                _scope.MaxParallelTables),
            CancellationToken.None,
            detail => PrintTableProgress(detail, ++completed, tableCount));
        stopwatch.Stop();

        PrintExecutionSummary(execResult, isUpdate, stopwatch.Elapsed);
        Console.WriteLine($"Plan saved to: {Path.GetFullPath(planOutputPath)}");
    }

    private static void PrintTableProgress(TableExecutionDetail detail, int completed, int total)
    {
        if (detail.Success)
        {
            Console.WriteLine($"  [{completed}/{total}] {detail.TableName,-40} {detail.RowsAffected,6} rows");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [{completed}/{total}] {detail.TableName,-40} FAILED: {detail.ErrorMessage}");
            Console.ResetColor();
        }
    }

    private static void PrintExecutionSummary(ExecutePlanResult result, bool isUpdate, TimeSpan elapsed)
    {
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

    internal static void WarnExternalCustomDependencyRoots(
        List<CustomDependencyGroup>? groups, int bufferSize)
    {
        if (groups is not { Count: > 0 })
            return;

        var rootDependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            if (group.Columns.Count < 2) continue;
            var source = group.Columns[0];
            if (!source.IsExternalRoot) continue;

            var rootKey = $"[{source.Table}].[{source.Column}]";
            if (!rootDependents.TryGetValue(rootKey, out var deps))
            {
                deps = [];
                rootDependents[rootKey] = deps;
            }

            for (var i = 1; i < group.Columns.Count; i++)
            {
                var dep = group.Columns[i];
                deps.Add($"[{dep.Table}].[{dep.Column}]");
            }
        }

        foreach (var (root, deps) in rootDependents)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"  Custom dep root: {root} -> dependents: {string.Join(", ", deps)} " +
                $"(will stream from DB; buffer={bufferSize})");
            Console.ResetColor();
        }
    }

    internal static void WarnUnsupportedColumns(List<TableInfo> tables)
    {
        foreach (var table in tables)
        {
            var skipped = table.Columns
                .Where(c => !c.IsAutoGenerated && PlanGenerator.IsUnsupportedType(c))
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


    internal static void PrintDataGenerationError(string tableName, DataGenerationException ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  {tableName,-40} FAILED at row {ex.RowIndex}");
        Console.ResetColor();

        if (ex.FailedColumn is { } col)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    Likely failed column: [{col.ColumnName}]");
            Console.WriteLine($"      SQL type:        {SqlTypeInfo.FormatSqlColumnType(col)}");
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
