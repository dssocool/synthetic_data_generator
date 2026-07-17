using System.IO;
using System.Text.Json;
using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI.Services;

public sealed class RuleHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void RecordModification(string ruleId, SavedRule rule, bool isNew)
    {
        var history = LoadModificationsMutable(ruleId);
        history.Add(new RuleModificationEntry
        {
            ModifiedAt = rule.ModifiedAt,
            Summary = isNew
                ? $"Created — {RuleSummaryBuilder.Build(rule)}"
                : RuleSummaryBuilder.Build(rule)
        });

        SaveModifications(ruleId, history);
    }

    public IReadOnlyList<RuleModificationEntry> LoadModifications(string ruleId) =>
        LoadModificationsMutable(ruleId);

    private static List<RuleModificationEntry> LoadModificationsMutable(string ruleId)
    {
        var path = GetModificationsPath(ruleId);
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<RuleModificationEntry>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void RecordExecution(string ruleId, RuleExecutionEntry entry)
    {
        var history = LoadExecutionsMutable(ruleId);
        history.Insert(0, entry);
        SaveExecutions(ruleId, history);
    }

    public IReadOnlyList<RuleExecutionEntry> LoadExecutions(string ruleId) =>
        LoadExecutionsMutable(ruleId);

    private static List<RuleExecutionEntry> LoadExecutionsMutable(string ruleId)
    {
        var path = GetExecutionsPath(ruleId);
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<RuleExecutionEntry>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void SaveModifications(string ruleId, List<RuleModificationEntry> entries)
    {
        var path = GetModificationsPath(ruleId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
    }

    private static void SaveExecutions(string ruleId, List<RuleExecutionEntry> entries)
    {
        var path = GetExecutionsPath(ruleId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
    }

    private static string GetHistoryDirectory(string ruleId) =>
        Path.Combine(RuleStorageService.GetRuleDirectory(ruleId), "history");

    private static string GetModificationsPath(string ruleId) =>
        Path.Combine(GetHistoryDirectory(ruleId), "modifications.json");

    private static string GetExecutionsPath(string ruleId) =>
        Path.Combine(GetHistoryDirectory(ruleId), "executions.json");
}

internal static class RuleSummaryBuilder
{
    public static string Build(SavedRule rule) =>
        rule.RuleType switch
        {
            RuleType.GenerateSyntheticData =>
                $"Rows: {rule.RowsPerTable}, Seed: {rule.Seed}, Tables: {rule.Summary}",
            RuleType.SimulatedSqlQuery =>
                $"Server: {rule.SimulatedServerName}, Query: {Truncate(rule.SqlQuery, 80)}",
            _ => rule.Name
        };

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";

        var trimmed = value.ReplaceLineEndings(" ").Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..(maxLength - 3)] + "...";
    }
}
