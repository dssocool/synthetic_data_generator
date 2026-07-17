using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI.Services;

public sealed class RuleStorageService
{
    private readonly RuleHistoryService _historyService = new();

    internal static readonly JsonSerializerOptions JsonSerializerOptions = new()
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
            CustomDependencies = state.CustomDependencies.ToList(),
            CustomValueLists = state.CustomValueLists
                .Select(v => new ColumnValueListConfig
                {
                    Column = v.Column,
                    File = v.File,
                    Values = v.Values?.ToList()
                })
                .ToList(),
            SimulatedServerName = state.SimulatedServerName,
            SqlQuery = state.SqlQuery
        };

        var ruleJsonPath = GetRuleJsonPath(ruleId);
        File.WriteAllText(ruleJsonPath, JsonSerializer.Serialize(rule, JsonSerializerOptions));

        if (state.RuleType == RuleType.GenerateSyntheticData)
        {
            File.WriteAllText(GetAppsettingsPath(ruleId), AppsettingsYamlBuilder.Build(state));
        }

        _historyService.RecordModification(ruleId, rule, isNew);

        return rule;
    }

    public SavedRule RevertToSnapshot(string ruleId, SavedRule snapshot)
    {
        var existing = TryLoadRule(ruleId)
            ?? throw new InvalidOperationException("Rule not found.");

        var rule = CloneRule(snapshot);
        rule.Id = ruleId;
        rule.CreatedAt = existing.CreatedAt;
        rule.ModifiedAt = DateTimeOffset.Now;

        File.WriteAllText(GetRuleJsonPath(ruleId), JsonSerializer.Serialize(rule, JsonSerializerOptions));

        if (rule.RuleType == RuleType.GenerateSyntheticData)
        {
            var state = new NewRuleWizardState();
            ApplyToWizardState(rule, state);
            File.WriteAllText(GetAppsettingsPath(ruleId), AppsettingsYamlBuilder.Build(state));
        }

        _historyService.RecordModification(
            ruleId,
            rule,
            isNew: false,
            summaryOverride: $"Reverted to {snapshot.ModifiedAt.LocalDateTime:g} — {RuleSummaryBuilder.Build(rule)}");

        return rule;
    }

    public static SavedRule CloneRule(SavedRule rule) =>
        new()
        {
            Id = rule.Id,
            Name = rule.Name,
            RuleType = rule.RuleType,
            CreatedAt = rule.CreatedAt,
            ModifiedAt = rule.ModifiedAt,
            ConnectionString = rule.ConnectionString,
            RowsPerTable = rule.RowsPerTable,
            Seed = rule.Seed,
            EnableDataOverwrite = rule.EnableDataOverwrite,
            Locale = rule.Locale,
            IncludeTables = rule.IncludeTables,
            CustomDependencies = rule.CustomDependencies?.ToList() ?? [],
            CustomValueLists = rule.CustomValueLists?
                .Select(v => new ColumnValueListConfig
                {
                    Column = v.Column,
                    File = v.File,
                    Values = v.Values?.ToList()
                })
                .ToList() ?? [],
            SimulatedServerName = rule.SimulatedServerName,
            SqlQuery = rule.SqlQuery
        };

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
        state.CustomDependencies = rule.CustomDependencies?.ToList() ?? [];
        state.CustomValueLists = rule.CustomValueLists?
            .Select(v => new ColumnValueListConfig
            {
                Column = v.Column,
                File = v.File,
                Values = v.Values?.ToList()
            })
            .ToList() ?? [];
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
            return JsonSerializer.Deserialize<SavedRule>(json, JsonSerializerOptions);
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
