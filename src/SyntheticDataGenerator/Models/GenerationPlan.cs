using System.Text.Json.Serialization;

namespace SyntheticDataGenerator.Models;

public class GenerationPlan
{
    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("locale")]
    public string Locale { get; set; } = "en";

    [JsonPropertyName("tables")]
    public List<TablePlan> Tables { get; set; } = [];
}

public class TablePlan
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = string.Empty;

    [JsonPropertyName("table")]
    public string Table { get; set; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; } = 100;

    [JsonPropertyName("columns")]
    public List<ColumnPlan> Columns { get; set; } = [];

    [JsonIgnore]
    public string FullName => $"{Schema}.{Table}";
}

public class ColumnPlan
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sqlType")]
    public string SqlType { get; set; } = string.Empty;

    [JsonPropertyName("maxLength")]
    public int MaxLength { get; set; }

    [JsonPropertyName("precision")]
    public byte Precision { get; set; }

    [JsonPropertyName("scale")]
    public byte Scale { get; set; }

    [JsonPropertyName("isNullable")]
    public bool IsNullable { get; set; }

    [JsonPropertyName("isIdentity")]
    public bool IsIdentity { get; set; }

    [JsonPropertyName("isPrimaryKey")]
    public bool IsPrimaryKey { get; set; }

    [JsonPropertyName("isComputed")]
    public bool IsComputed { get; set; }

    [JsonPropertyName("generator")]
    public string Generator { get; set; } = string.Empty;

    [JsonPropertyName("generatorArgs")]
    public Dictionary<string, object?> GeneratorArgs { get; set; } = new();
}
