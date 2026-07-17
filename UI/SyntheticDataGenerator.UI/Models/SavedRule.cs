using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI.Models;

public sealed class SavedRule
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RuleType RuleType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }

    public string ConnectionString { get; set; } = string.Empty;
    public int RowsPerTable { get; set; } = 100;
    public int Seed { get; set; } = 12345;
    public bool EnableDataOverwrite { get; set; }
    public string Locale { get; set; } = "en";
    public string IncludeTables { get; set; } = string.Empty;
    public List<string> CustomDependencies { get; set; } = [];
    public List<ColumnValueListConfig> CustomValueLists { get; set; } = [];

    public string SimulatedServerName { get; set; } = string.Empty;
    public string SqlQuery { get; set; } = string.Empty;

    public string TypeDisplayName => RuleType switch
    {
        RuleType.GenerateSyntheticData => "Generate synthetic data",
        RuleType.SimulatedSqlQuery => "Simulated SQL query",
        _ => "Unknown"
    };

    public string Summary => RuleType switch
    {
        RuleType.GenerateSyntheticData => BuildIncludeSummary(),
        RuleType.SimulatedSqlQuery => SimulatedServerName,
        _ => string.Empty
    };

    public string ModifiedAtDisplay => ModifiedAt.LocalDateTime.ToString("g");

    public bool CanExecute => RuleType == RuleType.GenerateSyntheticData;

    private string BuildIncludeSummary()
    {
        var patterns = AppsettingsYamlBuilder.ParseIncludeLines(IncludeTables);
        if (patterns.Count == 0)
            return "No tables selected";

        if (patterns.Count == 1)
            return patterns[0];

        return $"{patterns[0]} (+{patterns.Count - 1} more)";
    }
}
