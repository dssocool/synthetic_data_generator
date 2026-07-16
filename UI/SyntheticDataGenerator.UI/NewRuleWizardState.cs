namespace SyntheticDataGenerator.UI;

public enum RuleType
{
    None,
    GenerateSyntheticData,
    SimulatedSqlQuery
}

public sealed class NewRuleWizardState
{
    public RuleType RuleType { get; set; } = RuleType.None;

    public string ConnectionString { get; set; } = string.Empty;
    public int RowsPerTable { get; set; } = 100;
    public string IncludeTables { get; set; } = string.Empty;

    public string SimulatedServerName { get; set; } = string.Empty;
    public string SqlQuery { get; set; } = string.Empty;
}
