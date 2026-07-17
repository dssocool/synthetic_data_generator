using System.Text;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI.Services;

public static class AppsettingsYamlBuilder
{
    public static string Build(NewRuleWizardState state) =>
        Build(
            state.ConnectionString,
            state.IncludeTables,
            state.RowsPerTable,
            state.Seed,
            state.Locale,
            state.EnableDataOverwrite);

    public static string Build(SavedRule rule, int? rowsPerTable = null, int? seed = null) =>
        Build(
            rule.ConnectionString,
            rule.IncludeTables,
            rowsPerTable ?? rule.RowsPerTable,
            seed ?? rule.Seed,
            rule.Locale,
            rule.EnableDataOverwrite);

    private static string Build(
        string connectionString,
        string includeTablesText,
        int rowsPerTable,
        int seed,
        string locale,
        bool enableDataOverwrite)
    {
        var includeTables = ParseIncludeLines(includeTablesText);
        var sb = new StringBuilder();

        sb.AppendLine($"ConnectionString: {QuoteYaml(connectionString)}");
        sb.AppendLine(FormatIncludeSection(includeTables));
        sb.AppendLine("Exclude: []");
        sb.AppendLine($"RowsPerTable: {rowsPerTable}");
        sb.AppendLine($"Seed: {seed}");
        sb.AppendLine($"Locale: {locale}");
        if (enableDataOverwrite)
            sb.AppendLine("Mode: update");
        sb.AppendLine("CustomDependencies: []");
        sb.AppendLine();

        return sb.ToString();
    }

    public static IReadOnlyList<string> ParseIncludeLines(string includeTables)
    {
        if (string.IsNullOrWhiteSpace(includeTables))
            return [];

        return includeTables
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static string FormatIncludeSection(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return "Include: []";

        var sb = new StringBuilder();
        sb.AppendLine("Include:");
        foreach (var value in values)
            sb.AppendLine(FormatIncludeEntry(value));

        return sb.ToString().TrimEnd();
    }

    private static string FormatIncludeEntry(string line)
    {
        var parsed = IncludeScopePattern.Parse(line);
        if (!parsed.HasColumnSelection)
            return $"  - {QuoteYaml(SqlTableName.ToBracketedPattern(parsed.TablePattern))}";

        var sb = new StringBuilder();
        sb.AppendLine($"  - Table: {QuoteYaml(SqlTableName.ToBracketedPattern(parsed.TablePattern))}");
        sb.Append("    Columns:");
        foreach (var column in parsed.Columns!)
            sb.Append($"\n      - {QuoteYaml(column)}");

        return sb.ToString();
    }

    private static string QuoteYaml(string value)
    {
        if (CanUsePlainScalar(value))
            return value;

        if (!value.Contains('\''))
            return $"'{value}'";

        return $"\"{EscapeDoubleQuotedYaml(value)}\"";
    }

    private static bool CanUsePlainScalar(string value)
    {
        if (string.IsNullOrEmpty(value))
            return true;

        if (value.Contains('\n') || value.Contains('\r'))
            return false;

        if (value.Contains(':') || value.Contains('#') || value.Contains('"')
            || value.Contains('\\') || value.Contains('\'')
            || value.Contains('[') || value.Contains(']')
            || value.StartsWith(' ') || value.EndsWith(' '))
            return false;

        return true;
    }

    private static string EscapeDoubleQuotedYaml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
