using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class DataInserter
{
    private readonly string _connectionString;
    private readonly ColumnValueGenerator _valueGen;
    private readonly IReadOnlySet<string> _selfReferencingTables;
    private readonly Random _random = new();

    // schema.table -> list of PK row dictionaries (colName -> value)
    private readonly Dictionary<string, List<Dictionary<string, object>>> _generatedKeys = new();

    public DataInserter(
        string connectionString,
        ColumnValueGenerator valueGen,
        IReadOnlySet<string> selfReferencingTables)
    {
        _connectionString = connectionString;
        _valueGen = valueGen;
        _selfReferencingTables = selfReferencingTables;
    }

    public async Task<int> InsertTableFromPlanAsync(TablePlan tablePlan)
    {
        var table = TablePlanToTableInfo(tablePlan);
        var isSelfRef = tablePlan.Columns.Any(c =>
            c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase)
            && c.GeneratorArgs.TryGetValue("isSelfReferencing", out var selfRef)
            && IsTruthy(selfRef));

        var selfRefColumns = isSelfRef
            ? tablePlan.Columns.Where(c =>
                c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase)
                && c.GeneratorArgs.TryGetValue("isSelfReferencing", out var selfRef)
                && IsTruthy(selfRef)).ToList()
            : new List<ColumnPlan>();

        var columnsToInsert = tablePlan.Columns
            .Where(c => !c.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var firstPassColumns = isSelfRef
            ? columnsToInsert.Where(c => !selfRefColumns.Any(sr =>
                sr.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))).ToList()
            : columnsToInsert;

        var firstPassColumnInfos = firstPassColumns
            .Select(cp => table.Columns.First(c => c.Name.Equals(cp.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _generatedKeys.TryAdd(tablePlan.FullName, []);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var insertedCount = 0;

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            for (var i = 0; i < tablePlan.RowCount; i++)
            {
                var row = BuildRowFromPlan(firstPassColumns, tablePlan);

                var pkValues = await InsertRowAsync(
                    connection, transaction, table, firstPassColumnInfos, row);

                if (pkValues != null)
                    _generatedKeys[tablePlan.FullName].Add(pkValues);

                insertedCount++;
            }

            if (isSelfRef && _generatedKeys[tablePlan.FullName].Count > 0)
            {
                var selfRefFks = selfRefColumns.Select(c =>
                {
                    var args = c.GeneratorArgs;
                    return new ForeignKeyInfo
                    {
                        ParentSchema = tablePlan.Schema,
                        ParentTable = tablePlan.Table,
                        ParentColumn = c.Name,
                        ReferencedSchema = GetArgString(args, "referencedSchema"),
                        ReferencedTable = GetArgString(args, "referencedTable"),
                        ReferencedColumn = GetArgString(args, "referencedColumn"),
                    };
                }).ToList();

                await UpdateSelfReferencesAsync(connection, transaction, table, selfRefFks);
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return insertedCount;
    }

    public async Task<int> InsertTableAsync(TableInfo table, int rowCount)
    {
        var isSelfRef = _selfReferencingTables.Contains(table.FullName);
        var selfRefFks = isSelfRef
            ? table.ForeignKeys.Where(fk => fk.IsSelfReferencing).ToList()
            : [];

        var nonSelfRefFks = table.ForeignKeys.Where(fk => !fk.IsSelfReferencing).ToList();
        var fkColumnNames = new HashSet<string>(
            table.ForeignKeys.Select(fk => fk.ParentColumn), StringComparer.OrdinalIgnoreCase);

        var columnsToInsert = table.Columns
            .Where(c => !c.IsIdentity && !c.IsComputed)
            .ToList();

        var firstPassColumns = isSelfRef
            ? columnsToInsert.Where(c => !selfRefFks.Any(fk =>
                fk.ParentColumn.Equals(c.Name, StringComparison.OrdinalIgnoreCase))).ToList()
            : columnsToInsert;

        _generatedKeys.TryAdd(table.FullName, []);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var insertedCount = 0;

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            for (var i = 0; i < rowCount; i++)
            {
                var row = BuildRow(firstPassColumns, nonSelfRefFks, fkColumnNames, table);

                var pkValues = await InsertRowAsync(
                    connection, transaction, table, firstPassColumns, row);

                if (pkValues != null)
                    _generatedKeys[table.FullName].Add(pkValues);

                insertedCount++;
            }

            if (isSelfRef && _generatedKeys[table.FullName].Count > 0)
            {
                await UpdateSelfReferencesAsync(
                    connection, transaction, table, selfRefFks);
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return insertedCount;
    }

    private Dictionary<string, object?> BuildRowFromPlan(
        List<ColumnPlan> columns,
        TablePlan tablePlan)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var colPlan in columns)
        {
            if (colPlan.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase))
            {
                var refSchema = GetArgString(colPlan.GeneratorArgs, "referencedSchema");
                var refTable = GetArgString(colPlan.GeneratorArgs, "referencedTable");
                var refColumn = GetArgString(colPlan.GeneratorArgs, "referencedColumn");
                var refFullName = $"{refSchema}.{refTable}";

                if (_generatedKeys.TryGetValue(refFullName, out var parentRows) && parentRows.Count > 0)
                {
                    var parentRow = parentRows[_random.Next(parentRows.Count)];
                    if (parentRow.TryGetValue(refColumn, out var value))
                    {
                        row[colPlan.Name] = value;
                        continue;
                    }
                }

                if (colPlan.IsNullable)
                {
                    row[colPlan.Name] = DBNull.Value;
                    continue;
                }

                row[colPlan.Name] = _valueGen.GenerateFromPlan(colPlan) ?? DBNull.Value;
                continue;
            }

            if (colPlan.IsNullable && _random.NextDouble() < 0.1)
            {
                row[colPlan.Name] = DBNull.Value;
                continue;
            }

            row[colPlan.Name] = _valueGen.GenerateFromPlan(colPlan) ?? DBNull.Value;
        }

        return row;
    }

    private Dictionary<string, object?> BuildRow(
        List<ColumnInfo> columns,
        List<ForeignKeyInfo> nonSelfRefFks,
        HashSet<string> fkColumnNames,
        TableInfo table)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in columns)
        {
            if (fkColumnNames.Contains(col.Name))
            {
                var fk = nonSelfRefFks.FirstOrDefault(f =>
                    f.ParentColumn.Equals(col.Name, StringComparison.OrdinalIgnoreCase));

                if (fk != null)
                {
                    row[col.Name] = ResolveFkValue(fk, col);
                    continue;
                }
            }

            if (col.IsNullable && _random.NextDouble() < 0.1)
            {
                row[col.Name] = DBNull.Value;
                continue;
            }

            row[col.Name] = _valueGen.Generate(col) ?? DBNull.Value;
        }

        return row;
    }

    private object ResolveFkValue(ForeignKeyInfo fk, ColumnInfo col)
    {
        var refTable = fk.FullReferencedTableName;

        if (_generatedKeys.TryGetValue(refTable, out var parentRows) && parentRows.Count > 0)
        {
            var parentRow = parentRows[_random.Next(parentRows.Count)];
            if (parentRow.TryGetValue(fk.ReferencedColumn, out var value))
                return value;
        }

        if (col.IsNullable)
            return DBNull.Value;

        return _valueGen.Generate(col) ?? DBNull.Value;
    }

    private static TableInfo TablePlanToTableInfo(TablePlan tablePlan)
    {
        var table = new TableInfo
        {
            Schema = tablePlan.Schema,
            TableName = tablePlan.Table,
            Columns = tablePlan.Columns.Select(cp => new ColumnInfo
            {
                Name = cp.Name,
                SqlType = cp.SqlType,
                MaxLength = cp.MaxLength,
                Precision = cp.Precision,
                Scale = cp.Scale,
                IsNullable = cp.IsNullable,
                IsIdentity = cp.IsIdentity,
                IsPrimaryKey = cp.IsPrimaryKey,
                IsComputed = cp.IsComputed,
                FullTableName = tablePlan.FullName
            }).ToList(),
            PrimaryKeyColumns = tablePlan.Columns
                .Where(c => c.IsPrimaryKey)
                .Select(c => c.Name)
                .ToList(),
            ForeignKeys = tablePlan.Columns
                .Where(c => c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase))
                .Select(c => new ForeignKeyInfo
                {
                    ParentSchema = tablePlan.Schema,
                    ParentTable = tablePlan.Table,
                    ParentColumn = c.Name,
                    ReferencedSchema = GetArgString(c.GeneratorArgs, "referencedSchema"),
                    ReferencedTable = GetArgString(c.GeneratorArgs, "referencedTable"),
                    ReferencedColumn = GetArgString(c.GeneratorArgs, "referencedColumn"),
                }).ToList()
        };

        return table;
    }

    private static bool IsTruthy(object? value)
    {
        if (value is bool b) return b;
        if (value is JsonElement je) return je.ValueKind == JsonValueKind.True;
        if (value is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static string GetArgString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return string.Empty;
        if (value is string s) return s;
        if (value is JsonElement je) return je.GetString() ?? string.Empty;
        return value.ToString() ?? string.Empty;
    }

    private static async Task<Dictionary<string, object>?> InsertRowAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TableInfo table,
        List<ColumnInfo> columns,
        Dictionary<string, object?> row)
    {
        if (columns.Count == 0 && !table.HasIdentityPk)
            return null;

        var sb = new StringBuilder();
        var hasPkColumns = table.PrimaryKeyColumns.Count > 0;
        var identityPkCols = table.Columns
            .Where(c => c.IsPrimaryKey && c.IsIdentity)
            .Select(c => c.Name)
            .ToList();

        if (columns.Count > 0)
        {
            sb.Append($"INSERT INTO [{table.Schema}].[{table.TableName}] (");
            sb.Append(string.Join(", ", columns.Select(c => $"[{c.Name}]")));
            sb.Append(')');
        }
        else
        {
            sb.Append($"INSERT INTO [{table.Schema}].[{table.TableName}] DEFAULT VALUES");
        }

        if (hasPkColumns)
        {
            var allPkCols = table.PrimaryKeyColumns.Select(pk => $"INSERTED.[{pk}]");
            sb.Append(" OUTPUT ");
            sb.Append(string.Join(", ", allPkCols));
        }

        if (columns.Count > 0)
        {
            sb.Append(" VALUES (");
            sb.Append(string.Join(", ", columns.Select(c => $"@{c.Name}")));
            sb.Append(')');
        }

        await using var cmd = new SqlCommand(sb.ToString(), connection, transaction);

        foreach (var col in columns)
        {
            var paramValue = row.TryGetValue(col.Name, out var v) ? v ?? DBNull.Value : DBNull.Value;
            cmd.Parameters.AddWithValue($"@{col.Name}", paramValue);
        }

        if (hasPkColumns)
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var pkValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (var idx = 0; idx < table.PrimaryKeyColumns.Count; idx++)
                {
                    pkValues[table.PrimaryKeyColumns[idx]] = reader.GetValue(idx);
                }

                // Also include non-PK columns that we inserted (for FK references that
                // target unique columns rather than PKs)
                foreach (var col in columns)
                {
                    if (!pkValues.ContainsKey(col.Name) && row.TryGetValue(col.Name, out var rv) && rv != DBNull.Value && rv != null)
                    {
                        pkValues[col.Name] = rv;
                    }
                }

                return pkValues;
            }
        }
        else
        {
            await cmd.ExecuteNonQueryAsync();

            var pkValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in columns)
            {
                if (row.TryGetValue(col.Name, out var rv) && rv != DBNull.Value && rv != null)
                    pkValues[col.Name] = rv;
            }
            return pkValues.Count > 0 ? pkValues : null;
        }

        return null;
    }

    private async Task UpdateSelfReferencesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TableInfo table,
        List<ForeignKeyInfo> selfRefFks)
    {
        var rows = _generatedKeys[table.FullName];
        if (rows.Count < 2) return;

        // Update ~70% of rows with self-references, leave some as NULL roots
        var rowsToUpdate = rows
            .Skip(1) // keep at least the first row as root
            .Where(_ => _random.NextDouble() < 0.7)
            .ToList();

        foreach (var targetRow in rowsToUpdate)
        {
            foreach (var fk in selfRefFks)
            {
                if (!targetRow.TryGetValue(fk.ReferencedColumn, out var targetPkValue))
                    continue;

                // Pick a random parent that is not this same row
                var candidates = rows
                    .Where(r => r.TryGetValue(fk.ReferencedColumn, out var v)
                             && !Equals(v, targetPkValue))
                    .ToList();

                if (candidates.Count == 0) continue;

                var parentRow = candidates[_random.Next(candidates.Count)];
                var parentValue = parentRow[fk.ReferencedColumn];

                var sql = $"""
                    UPDATE [{table.Schema}].[{table.TableName}]
                       SET [{fk.ParentColumn}] = @ParentVal
                     WHERE [{fk.ReferencedColumn}] = @TargetPk
                    """;

                await using var cmd = new SqlCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("@ParentVal", parentValue);
                cmd.Parameters.AddWithValue("@TargetPk", targetPkValue);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
