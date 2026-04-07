namespace SyntheticDataGenerator.Models;

public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string SqlType { get; set; } = string.Empty;
    public int MaxLength { get; set; }
    public byte Precision { get; set; }
    public byte Scale { get; set; }
    public bool IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsComputed { get; set; }

    public string FullTableName { get; set; } = string.Empty;
}

public class ForeignKeyInfo
{
    public string FkName { get; set; } = string.Empty;

    public string ParentSchema { get; set; } = string.Empty;
    public string ParentTable { get; set; } = string.Empty;
    public string ParentColumn { get; set; } = string.Empty;

    public string ReferencedSchema { get; set; } = string.Empty;
    public string ReferencedTable { get; set; } = string.Empty;
    public string ReferencedColumn { get; set; } = string.Empty;

    public string FullParentTableName => $"{ParentSchema}.{ParentTable}";
    public string FullReferencedTableName => $"{ReferencedSchema}.{ReferencedTable}";

    public bool IsSelfReferencing => FullParentTableName == FullReferencedTableName;
}

public class TableInfo
{
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public List<ColumnInfo> Columns { get; set; } = [];
    public List<ForeignKeyInfo> ForeignKeys { get; set; } = [];
    public List<string> PrimaryKeyColumns { get; set; } = [];

    public string FullName => $"{Schema}.{TableName}";

    public bool HasIdentityPk =>
        Columns.Any(c => c.IsPrimaryKey && c.IsIdentity);
}
