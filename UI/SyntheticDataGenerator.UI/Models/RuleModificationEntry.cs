namespace SyntheticDataGenerator.UI.Models;

public sealed class RuleModificationEntry
{
    public DateTimeOffset ModifiedAt { get; set; }
    public string Summary { get; set; } = string.Empty;
}
