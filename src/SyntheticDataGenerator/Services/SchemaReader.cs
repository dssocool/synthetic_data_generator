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
        await ReadDefaultConstraints(connection, tables, schemaFilter);
        MarkSequenceDefaults(tables);
        await ReadCheckConstraints(connection, tables, schemaFilter);
        await ReadUniqueConstraints(connection, tables, schemaFilter);

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
                c.is_computed  AS IsComputed,
                CASE WHEN tp.name IN ('timestamp', 'rowversion') THEN 1 ELSE 0 END AS IsRowVersion,
                tp.is_user_defined AS IsUserDefined
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            INNER JOIN sys.columns c ON c.object_id = t.object_id
            INNER JOIN sys.types tp  ON tp.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0
              AND t.name NOT IN ('__EFMigrationsHistory', '__MigrationHistory', 'sysdiagrams')
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
                IsRowVersion = reader.GetInt32(10) == 1,
                IsUserDefined = reader.GetBoolean(11),
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

    private static async Task ReadDefaultConstraints(
        SqlConnection connection,
        Dictionary<string, TableInfo> tables,
        string? schemaFilter)
    {
        const string sql = """
            SELECT
                s.name  AS SchemaName,
                t.name  AS TableName,
                c.name  AS ColumnName,
                dc.definition AS Definition
            FROM sys.default_constraints dc
            INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id
                                     AND c.column_id = dc.parent_column_id
            INNER JOIN sys.tables t  ON t.object_id = dc.parent_object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
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
            var columnName = reader.GetString(2);
            var definition = reader.GetString(3);
            var fullName = $"{schema}.{tableName}";

            if (tables.TryGetValue(fullName, out var table))
            {
                var col = table.Columns.FirstOrDefault(c =>
                    c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                if (col != null)
                    col.DefaultDefinition = definition;
            }
        }
    }

    private static async Task ReadCheckConstraints(
        SqlConnection connection,
        Dictionary<string, TableInfo> tables,
        string? schemaFilter)
    {
        const string sql = """
            SELECT
                s.name  AS SchemaName,
                t.name  AS TableName,
                cc.name AS ConstraintName,
                cc.definition AS Definition
            FROM sys.check_constraints cc
            INNER JOIN sys.tables t  ON t.object_id = cc.parent_object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
              AND cc.is_disabled = 0
              AND (@SchemaFilter IS NULL OR @SchemaFilter = '' OR s.name = @SchemaFilter)
            ORDER BY s.name, t.name, cc.name
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@SchemaFilter", (object?)schemaFilter ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var fullName = $"{schema}.{tableName}";

            if (tables.TryGetValue(fullName, out var table))
            {
                table.CheckConstraints.Add(new CheckConstraintInfo
                {
                    Name = reader.GetString(2),
                    Definition = reader.GetString(3)
                });
            }
        }
    }

    private static async Task ReadUniqueConstraints(
        SqlConnection connection,
        Dictionary<string, TableInfo> tables,
        string? schemaFilter)
    {
        const string sql = """
            SELECT
                s.name  AS SchemaName,
                t.name  AS TableName,
                i.name  AS IndexName,
                c.name  AS ColumnName,
                ic.key_ordinal AS KeyOrdinal,
                i.has_filter AS HasFilter,
                i.filter_definition AS FilterDefinition
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id
                                            AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = i.object_id
                                     AND c.column_id = ic.column_id
            INNER JOIN sys.tables t  ON t.object_id = i.object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE i.is_unique = 1
              AND i.is_primary_key = 0
              AND t.is_ms_shipped = 0
              AND (@SchemaFilter IS NULL OR @SchemaFilter = '' OR s.name = @SchemaFilter)
            ORDER BY s.name, t.name, i.name, ic.key_ordinal
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@SchemaFilter", (object?)schemaFilter ?? DBNull.Value);

        var constraintData = new Dictionary<(string FullName, string IndexName), (List<string> Columns, string? Filter)>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var indexName = reader.GetString(2);
            var columnName = reader.GetString(3);
            var hasFilter = reader.GetBoolean(5);
            var filterDef = hasFilter && !reader.IsDBNull(6) ? reader.GetString(6) : null;
            var fullName = $"{schema}.{tableName}";

            var key = (fullName, indexName);
            if (!constraintData.TryGetValue(key, out var data))
            {
                data = ([], filterDef);
                constraintData[key] = data;
            }
            data.Columns.Add(columnName);
        }

        foreach (var ((fullName, indexName), (columns, filter)) in constraintData)
        {
            if (!tables.TryGetValue(fullName, out var table))
                continue;

            table.UniqueConstraints.Add(new UniqueConstraintInfo
            {
                Name = indexName,
                Columns = columns,
                FilterDefinition = filter
            });

            if (columns.Count == 1)
            {
                var col = table.Columns.FirstOrDefault(c =>
                    c.Name.Equals(columns[0], StringComparison.OrdinalIgnoreCase));
                if (col != null)
                    col.IsUnique = true;
            }
        }
    }

    private static void MarkSequenceDefaults(Dictionary<string, TableInfo> tables)
    {
        foreach (var table in tables.Values)
        {
            foreach (var col in table.Columns)
            {
                if (col.DefaultDefinition != null
                    && col.DefaultDefinition.Contains("NEXT VALUE FOR", StringComparison.OrdinalIgnoreCase))
                {
                    col.IsSequenceDefault = true;
                }
            }
        }
    }
}
