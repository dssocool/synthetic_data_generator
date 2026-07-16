using System.IO;
using Microsoft.Extensions.Configuration;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;
using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI.Services;

public sealed class SyntheticDataPreviewService
{
    public const int PreviewRowCount = 10;

    public async Task<SyntheticDataPreviewResult> GeneratePreviewAsync(
        NewRuleWizardState state,
        CancellationToken cancellationToken = default)
    {
        var includeTables = AppsettingsYamlBuilder.ParseIncludeLines(state.IncludeTables);
        if (includeTables.Count == 0)
        {
            return SyntheticDataPreviewResult.Failed(
                "Add at least one table to Include before generating a preview.");
        }

        var rulesDirectory = Path.Combine(AppContext.BaseDirectory, "rules", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rulesDirectory);

        var appsettingsPath = Path.Combine(rulesDirectory, "appsettings.yaml");
        await File.WriteAllTextAsync(appsettingsPath, AppsettingsYamlBuilder.Build(state), cancellationToken);

        var scope = LoadScopeConfig(appsettingsPath);
        var previewScope = new ScopeConfig(
            scope.Include,
            PreviewRowCount,
            scope.Seed,
            scope.Locale,
            scope.CustomDependencies,
            scope.CustomDependencyBufferSize,
            scope.CustomValueLists,
            maxParallelTables: 1,
            exclude: scope.Exclude);

        var planner = new DataGenerationPlanner();
        var validateResult = await planner.ValidateScopeAsync(
            new ValidateScopeCommand(state.ConnectionString, previewScope, "insert"),
            cancellationToken);

        if (!validateResult.IsValid)
        {
            return SyntheticDataPreviewResult.Failed(string.Join(Environment.NewLine, validateResult.Errors));
        }

        var planResult = await planner.GeneratePlanAsync(
            new GeneratePlanCommand(validateResult, previewScope, null, "insert"),
            cancellationToken);

        var plan = planResult.Plan;
        foreach (var table in plan.Tables)
            table.RowCount = PreviewRowCount;

        var selfRefTables = new HashSet<string>(
            plan.Tables
                .Where(t => t.Columns.Any(c =>
                    c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase)
                    && c.GeneratorArgs.TryGetValue("isSelfReferencing", out var selfRef)
                    && IsTruthy(selfRef)))
                .Select(t => t.FullName),
            StringComparer.OrdinalIgnoreCase);

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        valueGen.SetPlanBasePath(rulesDirectory);

        await using var inserter = new DataInserter(
            state.ConnectionString,
            valueGen,
            selfRefTables,
            planBasePath: rulesDirectory);

        var previewTables = new List<TablePreviewResult>();
        foreach (var tablePlan in plan.Tables.OrderBy(t => t.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var perTableGen = new ColumnValueGenerator(
                    DeriveTableSeed(plan.Seed, tablePlan.FullName),
                    plan.Locale);
                perTableGen.SetPlanBasePath(rulesDirectory);

                var generationResult = inserter.GenerateRows(tablePlan, perTableGen);
                previewTables.Add(new TablePreviewResult
                {
                    TableName = tablePlan.FullName,
                    DataTable = generationResult.DataTable.Copy()
                });
            }
            catch (DataGenerationException ex)
            {
                var message = ex.InnerException?.Message ?? ex.Message;
                return SyntheticDataPreviewResult.Failed(
                    $"Failed to generate preview rows for [{tablePlan.FullName}]: {message}");
            }
            catch (Exception ex)
            {
                return SyntheticDataPreviewResult.Failed(
                    $"Failed to generate preview rows for [{tablePlan.FullName}]: {ex.Message}");
            }
        }

        return new SyntheticDataPreviewResult
        {
            AppsettingsPath = appsettingsPath,
            Tables = previewTables
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
            maxParallelTables: 1,
            exclude: ScopeConfig.ParseExclude(config.GetSection("Exclude")));
    }

    private static int? DeriveTableSeed(int? planSeed, string tableFullName)
    {
        if (planSeed is null)
            return null;

        return planSeed.Value ^ StableHash(tableFullName);
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            const uint fnvOffset = 2166136261u;
            const uint fnvPrime = 16777619u;
            var hash = fnvOffset;
            foreach (var c in value.ToLowerInvariant())
                hash = (hash ^ c) * fnvPrime;

            return (int)hash;
        }
    }

    private static bool IsTruthy(object? value)
    {
        if (value is bool boolean)
            return boolean;

        if (value is string text)
            return text.Equals("true", StringComparison.OrdinalIgnoreCase);

        return false;
    }
}
