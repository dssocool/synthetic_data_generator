using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Configuration;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;
using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI.Services;

public sealed class SyntheticDataExecutionService
{
    public async Task<SyntheticDataExecutionResult> ExecuteAsync(
        SavedRule rule,
        int rowsPerTable,
        int seed,
        IProgress<SyntheticDataExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var includeTables = AppsettingsYamlBuilder.ParseIncludeLines(rule.IncludeTables);
        if (includeTables.Count == 0)
        {
            return SyntheticDataExecutionResult.Failed(
                "Add at least one table to Include before executing.");
        }

        if (rule.RuleType != RuleType.GenerateSyntheticData)
        {
            return SyntheticDataExecutionResult.Failed(
                "Only synthetic data generation rules can be executed.");
        }

        var ruleDirectory = RuleStorageService.GetRuleDirectory(rule.Id);
        Directory.CreateDirectory(ruleDirectory);

        var appsettingsPath = RuleStorageService.GetAppsettingsPath(rule.Id);
        await File.WriteAllTextAsync(
            appsettingsPath,
            AppsettingsYamlBuilder.Build(rule, rowsPerTable, seed),
            cancellationToken);

        var scope = LoadScopeConfig(appsettingsPath);
        var mode = LoadMode(appsettingsPath, rule.EnableDataOverwrite);
        var planner = new DataGenerationPlanner();

        progress?.Report(new SyntheticDataExecutionProgress("Validating scope..."));

        var validateResult = await planner.ValidateScopeAsync(
            new ValidateScopeCommand(rule.ConnectionString, scope, mode),
            cancellationToken);

        if (!validateResult.IsValid)
        {
            return SyntheticDataExecutionResult.Failed(
                string.Join(Environment.NewLine, validateResult.Errors));
        }

        progress?.Report(new SyntheticDataExecutionProgress("Generating plan..."));

        var planPath = Path.Combine(ruleDirectory, "plan.yaml");
        var planResult = await planner.GeneratePlanAsync(
            new GeneratePlanCommand(validateResult, scope, planPath, mode),
            cancellationToken);

        var sortedTables = planResult.Plan.Tables.OrderBy(t => t.Order).ToList();
        var tableCount = sortedTables.Count;
        var completed = 0;

        progress?.Report(new SyntheticDataExecutionProgress(
            $"Inserting data into {tableCount} table(s)...",
            completed,
            tableCount));

        var executor = new DataGenerationExecutor();
        var stopwatch = Stopwatch.StartNew();

        var execResult = await executor.ExecutePlanAsync(
            new ExecutePlanCommand(
                planResult.Plan,
                rule.ConnectionString,
                ruleDirectory,
                scope.CustomDependencyBufferSize,
                scope.MaxParallelTables),
            cancellationToken,
            detail =>
            {
                completed++;
                var status = detail.Success
                    ? $"{detail.RowsAffected} rows"
                    : detail.ErrorMessage ?? "Failed";
                progress?.Report(new SyntheticDataExecutionProgress(
                    detail.TableName,
                    completed,
                    tableCount,
                    status,
                    detail.Success));
            });

        stopwatch.Stop();

        var failedTable = execResult.Tables.FirstOrDefault(t => !t.Success);
        if (failedTable is not null)
        {
            return SyntheticDataExecutionResult.Failed(
                failedTable.ErrorMessage ?? $"Failed on table [{failedTable.TableName}].",
                execResult.TotalRowsAffected,
                execResult.Tables.Count,
                stopwatch.Elapsed);
        }

        return new SyntheticDataExecutionResult
        {
            Success = true,
            TotalRowsAffected = execResult.TotalRowsAffected,
            TableCount = execResult.Tables.Count,
            Elapsed = stopwatch.Elapsed
        };
    }

    private static ScopeConfig LoadScopeConfig(string appsettingsPath)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(appsettingsPath)!)
            .AddYamlFile(Path.GetFileName(appsettingsPath), optional: false)
            .Build();

        return new ScopeConfig(
            include: ScopeConfig.ParseInclude(config.GetSection("Include")),
            rowsPerTable: int.TryParse(config["RowsPerTable"], out var rows) ? rows : 100,
            seed: int.TryParse(config["Seed"], out var seed) ? seed : null,
            locale: config["Locale"] ?? "en",
            customDependencies: config.GetSection("CustomDependencies").Get<string[]>(),
            customDependencyBufferSize: int.TryParse(config["CustomDependencyBufferSize"], out var buffer)
                ? buffer
                : 10_000,
            customValueLists: ScopeConfig.ParseCustomValueLists(config.GetSection("CustomValueLists")),
            maxParallelTables: int.TryParse(config["MaxParallelTables"], out var parallel)
                ? parallel
                : Environment.ProcessorCount,
            exclude: ScopeConfig.ParseExclude(config.GetSection("Exclude")));
    }

    private static string LoadMode(string appsettingsPath, bool enableDataOverwrite)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(appsettingsPath)!)
            .AddYamlFile(Path.GetFileName(appsettingsPath), optional: false)
            .Build();

        var mode = config["Mode"];
        if (!string.IsNullOrWhiteSpace(mode))
            return mode;

        return enableDataOverwrite ? "update" : "insert";
    }
}

public sealed class SyntheticDataExecutionResult
{
    public bool Success { get; init; }
    public int TotalRowsAffected { get; init; }
    public int TableCount { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? ErrorMessage { get; init; }

    public static SyntheticDataExecutionResult Failed(
        string message,
        int totalRowsAffected = 0,
        int tableCount = 0,
        TimeSpan? elapsed = null) =>
        new()
        {
            Success = false,
            ErrorMessage = message,
            TotalRowsAffected = totalRowsAffected,
            TableCount = tableCount,
            Elapsed = elapsed ?? TimeSpan.Zero
        };
}

public sealed class SyntheticDataExecutionProgress
{
    public SyntheticDataExecutionProgress(
        string message,
        int completedTables = 0,
        int totalTables = 0,
        string? tableStatus = null,
        bool? tableSuccess = null)
    {
        Message = message;
        CompletedTables = completedTables;
        TotalTables = totalTables;
        TableStatus = tableStatus;
        TableSuccess = tableSuccess;
    }

    public string Message { get; }
    public int CompletedTables { get; }
    public int TotalTables { get; }
    public string? TableStatus { get; }
    public bool? TableSuccess { get; }
}
