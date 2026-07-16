using System.Data;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public static class SqlTypeInfo
{
    public static readonly HashSet<string> StringCompatibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "varchar", "nvarchar", "char", "nchar", "text", "ntext", "xml"
    };

    public static readonly HashSet<string> UnsupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "geography", "geometry", "hierarchyid"
    };

    public static bool IsUnsupportedType(ColumnInfo col) =>
        UnsupportedTypes.Contains(col.SqlType)
        || (col.IsUserDefined && !col.SqlType.Equals("sql_variant", StringComparison.OrdinalIgnoreCase));

    public static SqlDbType MapSqlType(string sqlType) =>
        sqlType.ToLowerInvariant() switch
        {
            "int"              => SqlDbType.Int,
            "bigint"           => SqlDbType.BigInt,
            "smallint"         => SqlDbType.SmallInt,
            "tinyint"          => SqlDbType.TinyInt,
            "bit"              => SqlDbType.Bit,
            "decimal"          => SqlDbType.Decimal,
            "numeric"          => SqlDbType.Decimal,
            "money"            => SqlDbType.Money,
            "smallmoney"       => SqlDbType.SmallMoney,
            "float"            => SqlDbType.Float,
            "real"             => SqlDbType.Real,
            "datetime"         => SqlDbType.DateTime,
            "datetime2"        => SqlDbType.DateTime2,
            "smalldatetime"    => SqlDbType.SmallDateTime,
            "date"             => SqlDbType.Date,
            "time"             => SqlDbType.Time,
            "datetimeoffset"   => SqlDbType.DateTimeOffset,
            "char"             => SqlDbType.Char,
            "nchar"            => SqlDbType.NChar,
            "varchar"          => SqlDbType.VarChar,
            "nvarchar"         => SqlDbType.NVarChar,
            "text"             => SqlDbType.Text,
            "ntext"            => SqlDbType.NText,
            "uniqueidentifier" => SqlDbType.UniqueIdentifier,
            "varbinary"        => SqlDbType.VarBinary,
            "binary"           => SqlDbType.Binary,
            "image"            => SqlDbType.Image,
            "xml"              => SqlDbType.Xml,
            "sql_variant"      => SqlDbType.Variant,
            _                  => SqlDbType.NVarChar,
        };

    public static string FormatSqlColumnType(IColumnMetadata col)
    {
        var type = col.SqlType.ToLowerInvariant();
        return type switch
        {
            "nvarchar" or "nchar" => col.MaxLength == -1
                ? $"{col.SqlType}(MAX)"
                : $"{col.SqlType}({col.MaxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => col.MaxLength == -1
                ? $"{col.SqlType}(MAX)"
                : $"{col.SqlType}({col.MaxLength})",
            "decimal" or "numeric" => $"{col.SqlType}({col.Precision},{col.Scale})",
            "datetime2" => col.Scale > 0
                ? $"{col.SqlType}({col.Scale})"
                : col.SqlType,
            "datetimeoffset" => col.Scale > 0
                ? $"{col.SqlType}({col.Scale})"
                : col.SqlType,
            "time" => col.Scale > 0
                ? $"{col.SqlType}({col.Scale})"
                : col.SqlType,
            _ => col.SqlType
        };
    }
}
