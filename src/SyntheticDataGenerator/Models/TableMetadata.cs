namespace SyntheticDataGenerator.Models;

public interface IColumnMetadata
{
    string Name { get; }
    string SqlType { get; }
    int MaxLength { get; }
    byte Precision { get; }
    byte Scale { get; }
    bool IsNullable { get; }
    bool IsPrimaryKey { get; }
    bool IsIdentity { get; }
    bool IsComputed { get; }
    bool IsRowVersion { get; }
    bool IsUnique { get; }
    bool IsSequenceDefault { get; }
    bool HasDefault { get; }
}

public class ColumnInfo : IColumnMetadata
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
    public bool IsRowVersion { get; set; }
    public bool IsUserDefined { get; set; }
    public bool IsUnique { get; set; }
    public bool IsSequenceDefault { get; set; }
    public string? DefaultDefinition { get; set; }

    public string FullTableName { get; set; } = string.Empty;

    public bool HasDefault => DefaultDefinition != null;
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

public class CompositeForeignKey
{
    public string FkName { get; set; } = string.Empty;
    public string ParentSchema { get; set; } = string.Empty;
    public string ParentTable { get; set; } = string.Empty;
    public string ReferencedSchema { get; set; } = string.Empty;
    public string ReferencedTable { get; set; } = string.Empty;
    public List<(string ParentColumn, string ReferencedColumn)> ColumnPairs { get; set; } = [];

    public string FullParentTableName => $"{ParentSchema}.{ParentTable}";
    public string FullReferencedTableName => $"{ReferencedSchema}.{ReferencedTable}";
    public bool IsSelfReferencing => FullParentTableName == FullReferencedTableName;
    public bool IsComposite => ColumnPairs.Count > 1;
}

public class CheckConstraintInfo
{
    public string Name { get; set; } = string.Empty;
    public string Definition { get; set; } = string.Empty;
}

public class UniqueConstraintInfo
{
    public string Name { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = [];
    public string? FilterDefinition { get; set; }
}

public class TableInfo
{
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public List<ColumnInfo> Columns { get; set; } = [];
    public List<ForeignKeyInfo> ForeignKeys { get; set; } = [];
    public List<string> PrimaryKeyColumns { get; set; } = [];
    public List<CheckConstraintInfo> CheckConstraints { get; set; } = [];
    public List<UniqueConstraintInfo> UniqueConstraints { get; set; } = [];

    public string FullName => $"{Schema}.{TableName}";

    public bool HasIdentityPk =>
        Columns.Any(c => c.IsPrimaryKey && c.IsIdentity);

    public bool HasSequencePk =>
        Columns.Any(c => c.IsPrimaryKey && c.IsSequenceDefault);

    public List<CompositeForeignKey> GetGroupedForeignKeys() =>
        ForeignKeys
            .GroupBy(fk => fk.FkName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CompositeForeignKey
            {
                FkName = g.Key,
                ParentSchema = g.First().ParentSchema,
                ParentTable = g.First().ParentTable,
                ReferencedSchema = g.First().ReferencedSchema,
                ReferencedTable = g.First().ReferencedTable,
                ColumnPairs = g.Select(fk => (fk.ParentColumn, fk.ReferencedColumn)).ToList()
            })
            .ToList();
}
