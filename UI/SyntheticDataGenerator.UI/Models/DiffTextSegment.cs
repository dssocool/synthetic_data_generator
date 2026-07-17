namespace SyntheticDataGenerator.UI.Models;

public sealed class DiffTextSegment
{
    public string Text { get; init; } = string.Empty;
    public bool IsDifferent { get; init; }
}
