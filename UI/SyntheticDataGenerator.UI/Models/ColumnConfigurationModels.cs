namespace SyntheticDataGenerator.UI.Models;

public sealed class ColumnValueListConfig
{
    public string Column { get; set; } = string.Empty;
    public string? File { get; set; }
    public List<string>? Values { get; set; }

    public bool HasInlineValues => Values is { Count: > 0 };
    public bool HasFile => !string.IsNullOrWhiteSpace(File);
}
