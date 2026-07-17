using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI.Services;

public sealed class RuleStorageService
{
    private readonly RuleHistoryService _historyService = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string RulesRootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyntheticDataGenerator",
            "rules");

    public SavedRule Save(NewRuleWizardState state, string? existingRuleId = null)
    {
        var isNew = string.IsNullOrWhiteSpace(existingRuleId);
        var ruleId = isNew ? Guid.NewGuid().ToString("N") : existingRuleId!;
        var ruleDirectory = GetRuleDirectory(ruleId);
        Directory.CreateDirectory(ruleDirectory);

        var now = DateTimeOffset.Now;
        var existing = isNew ? null : TryLoadRule(ruleId);
        var rule = new SavedRule
        {
            Id = ruleId,
            Name = existing?.Name ?? GenerateDefaultName(state),
            RuleType = state.RuleType,
            CreatedAt = existing?.CreatedAt ?? now,
            ModifiedAt = now,
            ConnectionString = state.ConnectionString,
            RowsPerTable = state.RowsPerTable,
            Seed = state.Seed,
            EnableDataOverwrite = state.EnableDataOverwrite,
            Locale = state.Locale,
            IncludeTables = state.IncludeTables,
            SimulatedServerName = state.SimulatedServerName,
            SqlQuery = state.SqlQuery
        };

        var ruleJsonPath = GetRuleJsonPath(ruleId);
        File.WriteAllText(ruleJsonPath, JsonSerializer.Serialize(rule, JsonOptions));

        if (state.RuleType == RuleType.GenerateSyntheticData)
        {
            File.WriteAllText(GetAppsettingsPath(ruleId), AppsettingsYamlBuilder.Build(state));
        }

        _historyService.RecordModification(ruleId, rule, isNew);

        return rule;
    }

    public IReadOnlyList<SavedRule> LoadAll()
    {
        if (!Directory.Exists(RulesRootDirectory))
            return [];

        return Directory.EnumerateDirectories(RulesRootDirectory)
            .Select(directory => TryLoadRule(Path.GetFileName(directory)))
            .Where(rule => rule is not null)
            .Cast<SavedRule>()
            .OrderByDescending(rule => rule.ModifiedAt)
            .ToList();
    }

    public SavedRule? LoadById(string ruleId) => TryLoadRule(ruleId);

    public void Delete(string ruleId)
    {
        var ruleDirectory = GetRuleDirectory(ruleId);
        if (Directory.Exists(ruleDirectory))
            Directory.Delete(ruleDirectory, recursive: true);
    }

    public static void ApplyToWizardState(SavedRule rule, NewRuleWizardState state)
    {
        state.RuleId = rule.Id;
        state.RuleType = rule.RuleType;
        state.ConnectionString = rule.ConnectionString;
        state.RowsPerTable = rule.RowsPerTable;
        state.Seed = rule.Seed;
        state.EnableDataOverwrite = rule.EnableDataOverwrite;
        state.Locale = rule.Locale;
        state.IncludeTables = rule.IncludeTables;
        state.SimulatedServerName = rule.SimulatedServerName;
        state.SqlQuery = rule.SqlQuery;
        state.AppsettingsPath = null;
        state.PreviewTables = null;
    }

    private static SavedRule? TryLoadRule(string ruleId)
    {
        var ruleJsonPath = GetRuleJsonPath(ruleId);
        if (!File.Exists(ruleJsonPath))
            return null;

        try
        {
            var json = File.ReadAllText(ruleJsonPath);
            return JsonSerializer.Deserialize<SavedRule>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string GetRuleDirectory(string ruleId) =>
        Path.Combine(RulesRootDirectory, ruleId);

    public static string GetAppsettingsPath(string ruleId) =>
        Path.Combine(GetRuleDirectory(ruleId), "appsettings.yaml");

    private static string GetRuleJsonPath(string ruleId) =>
        Path.Combine(GetRuleDirectory(ruleId), "rule.json");

    private static string GenerateDefaultName(NewRuleWizardState state) =>
        state.RuleType switch
        {
            RuleType.GenerateSyntheticData =>
                $"Synthetic Data - {GetIncludeSummary(state.IncludeTables)}",
            RuleType.SimulatedSqlQuery =>
                $"Query - {state.SimulatedServerName}",
            _ => "New Rule"
        };

    private static string GetIncludeSummary(string includeTables)
    {
        var patterns = AppsettingsYamlBuilder.ParseIncludeLines(includeTables);
        if (patterns.Count == 0)
            return "untitled";

        if (patterns.Count == 1)
            return patterns[0];

        return $"{patterns[0]} (+{patterns.Count - 1} more)";
    }
}
