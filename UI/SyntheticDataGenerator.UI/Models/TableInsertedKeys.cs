namespace SyntheticDataGenerator.UI.Models;

public sealed class TableInsertedKeys
{
    public string TableName { get; set; } = string.Empty;
    public bool HasPrimaryKey { get; set; } = true;
    public List<string> PrimaryKeys { get; set; } = [];
}
