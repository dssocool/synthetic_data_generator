using System.Text;

namespace SyntheticDataGenerator.UI.Services;

public static class AppsettingsYamlBuilder
{
    public static string Build(NewRuleWizardState state)
    {
        var includeTables = ParseIncludeLines(state.IncludeTables);
        var sb = new StringBuilder();

        sb.AppendLine($"ConnectionString: {QuoteYaml(state.ConnectionString)}");
        sb.AppendLine(FormatYamlSequence("Include", includeTables));
        sb.AppendLine("Exclude: []");
        sb.AppendLine($"RowsPerTable: {state.RowsPerTable}");
        sb.AppendLine($"Seed: {state.Seed}");
        sb.AppendLine($"Locale: {state.Locale}");
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

    private static string FormatYamlSequence(string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return $"{key}: []";

        var sb = new StringBuilder();
        sb.AppendLine($"{key}:");
        foreach (var value in values)
            sb.AppendLine($"  - {QuoteYaml(value)}");

        return sb.ToString().TrimEnd();
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
            || value.StartsWith(' ') || value.EndsWith(' '))
            return false;

        return true;
    }

    private static string EscapeDoubleQuotedYaml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
