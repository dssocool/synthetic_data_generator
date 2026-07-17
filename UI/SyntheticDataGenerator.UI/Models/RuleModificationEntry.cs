namespace SyntheticDataGenerator.UI.Models;

public sealed class RuleModificationEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset ModifiedAt { get; set; }
    public string Summary { get; set; } = string.Empty;
    public SavedRule? Snapshot { get; set; }
}
