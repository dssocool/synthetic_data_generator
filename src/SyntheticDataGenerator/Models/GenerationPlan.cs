using YamlDotNet.Serialization;

namespace SyntheticDataGenerator.Models;

public class GenerationPlan
{
    [YamlMember(Alias = "mode")]
    public string Mode { get; set; } = "bootstrap";

    [YamlMember(Alias = "seed")]
    public int? Seed { get; set; }

    [YamlMember(Alias = "locale")]
    public string Locale { get; set; } = "en";

    [YamlMember(Alias = "tables")]
    public List<TablePlan> Tables { get; set; } = [];

    [YamlMember(Alias = "externalDependencies")]
    public List<ExternalDependency>? ExternalDependencies { get; set; }
}

public class TablePlan
{
    [YamlMember(Alias = "table")]
    public string Table { get; set; } = string.Empty;

    [YamlMember(Alias = "order")]
    public int Order { get; set; }

    [YamlMember(Alias = "rowCount")]
    public int RowCount { get; set; } = 100;

    [YamlMember(Alias = "columns")]
    public List<ColumnPlan> Columns { get; set; } = [];

    [YamlMember(Alias = "uniqueConstraints")]
    public List<UniqueConstraintPlan>? UniqueConstraints { get; set; }

    [YamlIgnore]
    public string FullName => Table;

    [YamlIgnore]
    public string Schema => Table.Contains('.') ? Table[..Table.IndexOf('.')] : string.Empty;

    [YamlIgnore]
    public string TableName => Table.Contains('.') ? Table[(Table.IndexOf('.') + 1)..] : Table;
}

public class UniqueConstraintPlan
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "columns")]
    public List<string> Columns { get; set; } = [];

    [YamlMember(Alias = "filterDefinition")]
    public string? FilterDefinition { get; set; }
}

public class ExternalDependency
{
    [YamlMember(Alias = "fkName")]
    public string FkName { get; set; } = string.Empty;

    /// <summary>
    /// "outbound" = scoped table FK references external table.
    /// "inbound"  = external table FK references scoped table.
    /// </summary>
    [YamlMember(Alias = "direction")]
    public string Direction { get; set; } = string.Empty;

    [YamlMember(Alias = "scopedTable")]
    public string ScopedTable { get; set; } = string.Empty;

    [YamlMember(Alias = "scopedColumn")]
    public string ScopedColumn { get; set; } = string.Empty;

    [YamlMember(Alias = "externalTable")]
    public string ExternalTable { get; set; } = string.Empty;

    [YamlMember(Alias = "externalColumn")]
    public string ExternalColumn { get; set; } = string.Empty;
}

public class ColumnPlan : IColumnMetadata
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "sqlType")]
    public string SqlType { get; set; } = string.Empty;

    [YamlMember(Alias = "maxLength")]
    public int MaxLength { get; set; }

    [YamlMember(Alias = "precision")]
    public byte Precision { get; set; }

    [YamlMember(Alias = "scale")]
    public byte Scale { get; set; }

    [YamlMember(Alias = "isNullable")]
    public bool IsNullable { get; set; }

    [YamlMember(Alias = "isIdentity")]
    public bool IsIdentity { get; set; }

    [YamlMember(Alias = "isPrimaryKey")]
    public bool IsPrimaryKey { get; set; }

    [YamlMember(Alias = "isComputed")]
    public bool IsComputed { get; set; }

    [YamlMember(Alias = "isRowVersion")]
    public bool IsRowVersion { get; set; }

    [YamlMember(Alias = "hasDefault")]
    public bool HasDefault { get; set; }

    [YamlMember(Alias = "isSequenceDefault")]
    public bool IsSequenceDefault { get; set; }

    [YamlMember(Alias = "isUnique")]
    public bool IsUnique { get; set; }

    [YamlMember(Alias = "generator")]
    public string Generator { get; set; } = string.Empty;

    [YamlMember(Alias = "generatorArgs")]
    public Dictionary<string, object?> GeneratorArgs { get; set; } = new();

    [YamlMember(Alias = "valuesFile")]
    public string? ValuesFile { get; set; }
}
