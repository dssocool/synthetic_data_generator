using System.Text;
using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI.Services;

public static class RuleDiffBuilder
{
    public static IReadOnlyList<RuleFieldDiff> Build(SavedRule historical, SavedRule current)
    {
        var diffs = new List<RuleFieldDiff>();

        AddDiff(diffs, "Name", historical.Name, current.Name);
        AddDiff(diffs, "Rule Type", historical.TypeDisplayName, current.TypeDisplayName);

        if (historical.RuleType == RuleType.GenerateSyntheticData ||
            current.RuleType == RuleType.GenerateSyntheticData)
        {
            AddDiff(diffs, "Connection String", historical.ConnectionString, current.ConnectionString);
            AddDiff(diffs, "Rows Per Table", historical.RowsPerTable, current.RowsPerTable);
            AddDiff(diffs, "Seed", historical.Seed, current.Seed);
            AddDiff(diffs, "Enable Data Overwrite", historical.EnableDataOverwrite, current.EnableDataOverwrite);
            AddDiff(diffs, "Locale", historical.Locale, current.Locale);
            AddDiff(diffs, "Include Tables", historical.IncludeTables, current.IncludeTables);
            AddDiff(diffs, "Custom Dependencies", FormatList(historical.CustomDependencies), FormatList(current.CustomDependencies));
            AddDiff(diffs, "Custom Value Lists", FormatValueLists(historical.CustomValueLists), FormatValueLists(current.CustomValueLists));
        }

        if (historical.RuleType == RuleType.SimulatedSqlQuery ||
            current.RuleType == RuleType.SimulatedSqlQuery)
        {
            AddDiff(diffs, "Simulated Server", historical.SimulatedServerName, current.SimulatedServerName);
            AddDiff(diffs, "SQL Query", historical.SqlQuery, current.SqlQuery);
        }

        return diffs;
    }

    public static bool AreConfigurationsEqual(SavedRule historical, SavedRule current)
    {
        if (historical.RuleType != current.RuleType)
            return false;

        return historical.RuleType switch
        {
            RuleType.GenerateSyntheticData =>
                historical.Name == current.Name &&
                historical.ConnectionString == current.ConnectionString &&
                historical.RowsPerTable == current.RowsPerTable &&
                historical.Seed == current.Seed &&
                historical.EnableDataOverwrite == current.EnableDataOverwrite &&
                historical.Locale == current.Locale &&
                historical.IncludeTables == current.IncludeTables &&
                SequenceEqual(historical.CustomDependencies, current.CustomDependencies) &&
                ValueListsEqual(historical.CustomValueLists, current.CustomValueLists),
            RuleType.SimulatedSqlQuery =>
                historical.Name == current.Name &&
                historical.SimulatedServerName == current.SimulatedServerName &&
                historical.SqlQuery == current.SqlQuery,
            _ => historical.Name == current.Name
        };
    }

    private static void AddDiff<T>(List<RuleFieldDiff> diffs, string field, T historical, T current)
    {
        var historicalText = FormatValue(historical);
        var currentText = FormatValue(current);
        var isChanged = !string.Equals(historicalText, currentText, StringComparison.Ordinal);
        var (historicalSegments, currentSegments) = isChanged
            ? TextDiffBuilder.BuildSideBySide(historicalText, currentText)
            : (TextDiffBuilder.BuildSingleSide(historicalText), TextDiffBuilder.BuildSingleSide(currentText));

        diffs.Add(new RuleFieldDiff
        {
            Field = field,
            HistoricalValue = historicalText,
            CurrentValue = currentText,
            IsChanged = isChanged,
            HistoricalSegments = historicalSegments,
            CurrentSegments = currentSegments
        });
    }

    private static string FormatValue<T>(T value) =>
        value switch
        {
            null => "(empty)",
            bool boolean => boolean ? "Yes" : "No",
            string text when string.IsNullOrWhiteSpace(text) => "(empty)",
            string text => text,
            _ => value.ToString() ?? "(empty)"
        };

    private static string FormatList(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return "(none)";

        return string.Join(Environment.NewLine, values);
    }

    private static string FormatValueLists(IReadOnlyList<ColumnValueListConfig>? valueLists)
    {
        if (valueLists is null || valueLists.Count == 0)
            return "(none)";

        var builder = new StringBuilder();
        for (var index = 0; index < valueLists.Count; index++)
        {
            if (index > 0)
                builder.AppendLine();

            var config = valueLists[index];
            builder.Append(config.Column);
            if (!string.IsNullOrWhiteSpace(config.File))
                builder.Append($" -> {config.File}");
            else if (config.Values is { Count: > 0 })
                builder.Append($" -> [{string.Join(", ", config.Values)}]");
        }

        return builder.ToString();
    }

    private static bool SequenceEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        left ??= [];
        right ??= [];
        return left.SequenceEqual(right);
    }

    private static bool ValueListsEqual(
        IReadOnlyList<ColumnValueListConfig>? left,
        IReadOnlyList<ColumnValueListConfig>? right)
    {
        left ??= [];
        right ??= [];

        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var leftConfig = left[index];
            var rightConfig = right[index];
            if (leftConfig.Column != rightConfig.Column ||
                leftConfig.File != rightConfig.File ||
                !SequenceEqual(leftConfig.Values, rightConfig.Values))
            {
                return false;
            }
        }

        return true;
    }
}
