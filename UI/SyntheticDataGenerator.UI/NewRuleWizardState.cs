namespace SyntheticDataGenerator.UI;

public enum RuleType
{
    None,
    GenerateSyntheticData,
    SimulatedSqlQuery
}

public sealed class NewRuleWizardState
{
    public string? RuleId { get; set; }
    public RuleType RuleType { get; set; } = RuleType.None;

    public string ConnectionString { get; set; } = string.Empty;
    public int RowsPerTable { get; set; } = 100;
    public int Seed { get; set; } = 12345;
    public bool EnableDataOverwrite { get; set; }
    public string Locale { get; set; } = "en";
    public string IncludeTables { get; set; } = string.Empty;
    public List<string> CustomDependencies { get; set; } = [];
    public List<Models.ColumnValueListConfig> CustomValueLists { get; set; } = [];

    public string SimulatedServerName { get; set; } = string.Empty;
    public string SqlQuery { get; set; } = string.Empty;

    public string? AppsettingsPath { get; set; }
    public IReadOnlyList<Models.TablePreviewResult>? PreviewTables { get; set; }
}
