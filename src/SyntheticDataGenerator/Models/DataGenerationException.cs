namespace SyntheticDataGenerator.Models;

public class ColumnFailureDetail : IColumnMetadata
{
    public string ColumnName { get; init; } = string.Empty;
    public string SqlType { get; init; } = string.Empty;
    public int MaxLength { get; init; }
    public byte Precision { get; init; }
    public byte Scale { get; init; }
    public string Generator { get; init; } = string.Empty;
    public string? GeneratedValueType { get; init; }
    public string? GeneratedValuePreview { get; init; }

    string IColumnMetadata.Name => ColumnName;
    bool IColumnMetadata.IsNullable => false;
    bool IColumnMetadata.IsPrimaryKey => false;
    bool IColumnMetadata.IsIdentity => false;
    bool IColumnMetadata.IsComputed => false;
    bool IColumnMetadata.IsRowVersion => false;
    bool IColumnMetadata.IsUnique => false;
    bool IColumnMetadata.IsSequenceDefault => false;
    bool IColumnMetadata.HasDefault => false;
}

public class DataGenerationException : Exception
{
    public string TableName { get; }
    public int RowIndex { get; }
    public ColumnFailureDetail? FailedColumn { get; }

    public DataGenerationException(
        string tableName,
        int rowIndex,
        ColumnFailureDetail? failedColumn,
        Exception innerException)
        : base(BuildMessage(tableName, rowIndex, failedColumn, innerException), innerException)
    {
        TableName = tableName;
        RowIndex = rowIndex;
        FailedColumn = failedColumn;
    }

    private static string BuildMessage(
        string tableName, int rowIndex, ColumnFailureDetail? failedColumn, Exception inner)
    {
        var msg = $"Failed inserting row {rowIndex} into {tableName}";
        if (failedColumn is not null)
            msg += $" (column: [{failedColumn.ColumnName}], type: {failedColumn.SqlType}, " +
                   $"generator: {failedColumn.Generator}, " +
                   $"value type: {failedColumn.GeneratedValueType ?? "null"}, " +
                   $"value: {failedColumn.GeneratedValuePreview ?? "null"})";
        msg += $": {inner.Message}";
        return msg;
    }
}
