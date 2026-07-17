namespace SyntheticDataGenerator.UI.Models;

public sealed class RuleFieldDiff
{
    public string Field { get; set; } = string.Empty;
    public string HistoricalValue { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public bool IsChanged { get; set; }
    public IReadOnlyList<DiffTextSegment> HistoricalSegments { get; set; } = [];
    public IReadOnlyList<DiffTextSegment> CurrentSegments { get; set; } = [];
}
