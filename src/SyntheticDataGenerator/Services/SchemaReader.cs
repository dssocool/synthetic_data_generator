using Microsoft.Data.SqlClient;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class SchemaReader
{
    private readonly string _connectionString;

    public SchemaReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<TableInfo>> ReadSchemaAsync(string? schemaFilter = null)
    {
        var tables = new Dictionary<string, TableInfo>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await ReadTablesAndColumns(connection, tables, schemaFilter);
        await ReadPrimaryKeys(connection, tables, schemaFilter);
        await ReadForeignKeys(connection, tables, schemaFilter);

        return tables.Values.ToList();
    }

    private static async Task ReadTablesAndColumns(
        SqlConnection connection,
        Dictionary<string, TableInfo> tables,
        string? schemaFilter)
    {
        const string sql = """
            SELECT
                s.name       AS SchemaName,
                t.name       AS TableName,
                c.name       AS ColumnName,
                tp.name      AS TypeName,
                c.max_length AS MaxLength,
                c.precision  AS Precision,
                c.scale      AS Scale,
                c.is_nullable  AS IsNullable,
                c.is_identity  AS IsIdentity,
                c.is_computed  AS IsComputed
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            INNER JOIN sys.columns c ON c.object_id = t.object_id
            INNER JOIN sys.types tp  ON tp.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0
              AND (@SchemaFilter IS NULL OR @SchemaFilter = '' OR s.name = @SchemaFilter)
            ORDER BY s.name, t.name, c.column_id
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@SchemaFilter", (object?)schemaFilter ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var fullName = $"{schema}.{tableName}";

            if (!tables.TryGetValue(fullName, out var table))
            {
                table = new TableInfo { Schema = schema, TableName = tableName };
                tables[fullName] = table;
            }

            table.Columns.Add(new ColumnInfo
            {
                Name = reader.GetString(2),
                SqlType = reader.GetString(3),
                MaxLength = reader.GetInt16(4),
                Precision = reader.GetByte(5),
                Scale = reader.GetByte(6),
                IsNullable = reader.GetBoolean(7),
                IsIdentity = reader.GetBoolean(8),
                IsComputed = reader.GetBoolean(9),
                FullTableName = fullName
            });
        }
    }

    private static async Task ReadPrimaryKeys(
        SqlConnection connection,
        Dictionary<string, TableInfo> tables,
        string? schemaFilter)
    {
        const string sql = """
            SELECT
                s.name  AS SchemaName,
                t.name  AS TableName,
                c.name  AS ColumnName
            FROM sys.key_constraints kc
            INNER JOIN sys.tables t        ON t.object_id = kc.parent_object_id
            INNER JOIN sys.schemas s       ON s.schema_id = t.schema_id
            INNER JOIN sys.index_columns ic ON ic.object_id = t.object_id
                                           AND ic.index_id = kc.unique_index_id
            INNER JOIN sys.columns c       ON c.object_id = t.object_id
                                           AND c.column_id = ic.column_id
            WHERE kc.type = 'PK'
              AND t.is_ms_shipped = 0
              AND (@SchemaFilter IS NULL OR @SchemaFilter = '' OR s.name = @SchemaFilter)
            ORDER BY s.name, t.name, ic.key_ordinal
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@SchemaFilter", (object?)schemaFilter ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var columnName = reader.GetString(2);
            var fullName = $"{schema}.{tableName}";

            if (tables.TryGetValue(fullName, out var table))
            {
                table.PrimaryKeyColumns.Add(columnName);

                var col = table.Columns.FirstOrDefault(c => c.Name == columnName);
                if (col != null)
                    col.IsPrimaryKey = true;
            }
        }
    }

    private static async Task ReadForeignKeys(
        SqlConnection connection,
        Dictionary<string, TableInfo> tables,
        string? schemaFilter)
    {
        const string sql = """
            SELECT
                fk.name            AS FkName,
                s_parent.name      AS ParentSchema,
                t_parent.name      AS ParentTable,
                c_parent.name      AS ParentColumn,
                s_ref.name         AS ReferencedSchema,
                t_ref.name         AS ReferencedTable,
                c_ref.name         AS ReferencedColumn
            FROM sys.foreign_keys fk
            INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.tables t_parent         ON t_parent.object_id = fk.parent_object_id
            INNER JOIN sys.schemas s_parent        ON s_parent.schema_id = t_parent.schema_id
            INNER JOIN sys.columns c_parent        ON c_parent.object_id = fkc.parent_object_id
                                                   AND c_parent.column_id = fkc.parent_column_id
            INNER JOIN sys.tables t_ref            ON t_ref.object_id = fk.referenced_object_id
            INNER JOIN sys.schemas s_ref           ON s_ref.schema_id = t_ref.schema_id
            INNER JOIN sys.columns c_ref           ON c_ref.object_id = fkc.referenced_object_id
                                                   AND c_ref.column_id = fkc.referenced_column_id
            WHERE t_parent.is_ms_shipped = 0
              AND (@SchemaFilter IS NULL OR @SchemaFilter = '' OR s_parent.name = @SchemaFilter)
            ORDER BY fk.name, fkc.constraint_column_id
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@SchemaFilter", (object?)schemaFilter ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var parentSchema = reader.GetString(1);
            var parentTable = reader.GetString(2);
            var fullParent = $"{parentSchema}.{parentTable}";

            if (tables.TryGetValue(fullParent, out var table))
            {
                table.ForeignKeys.Add(new ForeignKeyInfo
                {
                    FkName = reader.GetString(0),
                    ParentSchema = parentSchema,
                    ParentTable = parentTable,
                    ParentColumn = reader.GetString(3),
                    ReferencedSchema = reader.GetString(4),
                    ReferencedTable = reader.GetString(5),
                    ReferencedColumn = reader.GetString(6)
                });
            }
        }
    }
}
