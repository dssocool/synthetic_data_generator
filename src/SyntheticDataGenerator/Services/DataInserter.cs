using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class DataInserter
{
    private readonly string _connectionString;
    private readonly ColumnValueGenerator _valueGen;
    private readonly IReadOnlySet<string> _selfReferencingTables;
    private readonly Random _random = new();

    internal readonly Dictionary<string, List<Dictionary<string, object>>> _generatedKeys = new();
    private readonly Dictionary<string, HashSet<string>> _generatedPkSets = new();
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _generatedUniqueSets = new();

    private const int MaxPkRetries = 100;

    public DataInserter(
        string connectionString,
        ColumnValueGenerator valueGen,
        IReadOnlySet<string> selfReferencingTables)
    {
        _connectionString = connectionString;
        _valueGen = valueGen;
        _selfReferencingTables = selfReferencingTables;
    }

    /// <summary>
    /// Stage generated data into a temp table. Returns (tempTableName, connection, transaction, table, stagedCount).
    /// The caller must call InsertFromTempTableAsync or UpdateFromTempTableAsync, then commit/rollback.
    /// </summary>
    public async Task<StagingResult> StageToTempTableAsync(TablePlan tablePlan)
    {
        var table = TablePlanToTableInfo(tablePlan);
        var fullName = tablePlan.FullName;
        var tempTableName = $"#{tablePlan.TableName}";

        var isSelfRef = tablePlan.Columns.Any(c =>
            c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase)
            && c.GeneratorArgs.TryGetValue("isSelfReferencing", out var selfRef)
            && Helpers.IsTruthy(selfRef));

        var selfRefColumnNames = isSelfRef
            ? tablePlan.Columns.Where(c =>
                c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase)
                && c.GeneratorArgs.TryGetValue("isSelfReferencing", out var selfRef)
                && Helpers.IsTruthy(selfRef)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var columnsToGenerate = tablePlan.Columns
            .Where(c => !c.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var firstPassColumns = isSelfRef
            ? columnsToGenerate.Where(c => !selfRefColumnNames.Contains(c.Name)).ToList()
            : columnsToGenerate;

        var allDataColumnInfos = columnsToGenerate
            .Select(cp => table.Columns.First(c => c.Name.Equals(cp.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var uniqueConstraints = BuildUniqueConstraintsFromPlan(tablePlan);
        var fkGroups = BuildFkGroupsFromPlan(firstPassColumns);
        var customDepGroups = BuildCustomDepGroupsFromPlan(firstPassColumns);

        _generatedKeys.TryAdd(fullName, []);
        _generatedPkSets.TryAdd(fullName, []);
        InitUniqueConstraintSets(fullName, uniqueConstraints);

        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            var createSql = BuildCreateTempTableSql(tempTableName, allDataColumnInfos);
            await using (var cmd = new SqlCommand(createSql, connection, transaction))
                await cmd.ExecuteNonQueryAsync();

            var stagedCount = 0;
            for (var i = 0; i < tablePlan.RowCount; i++)
            {
                Dictionary<string, object?> row;
                try
                {
                    var attempt = 0;
                    string? pkKey;
                    bool uniqueOk;
                    do
                    {
                        row = BuildRowFromFkGroups(firstPassColumns, fkGroups,
                            col => _valueGen.GenerateFromPlan((ColumnPlan)col) ?? DBNull.Value,
                            tablePlan.RowCount,
                            customDepGroups);
                        pkKey = BuildPkKeyFromRow(table, row);
                        uniqueOk = TryAddUniqueKeys(fullName, uniqueConstraints, row, attempt > 0);
                        attempt++;
                    } while ((!uniqueOk || (pkKey != null
                                            && !_generatedPkSets[fullName].Add(pkKey)))
                             && attempt < MaxPkRetries);

                    if (attempt >= MaxPkRetries)
                        throw new InvalidOperationException(
                            $"Could not generate unique values for [{fullName}] " +
                            $"after {MaxPkRetries} attempts. Consider reducing RowsPerTable or " +
                            $"using a wider value range.");
                }
                catch (DataGenerationException) { throw; }
                catch (Exception ex)
                {
                    throw new DataGenerationException(fullName, i, null, ex);
                }

                try
                {
                    await InsertRowIntoTempAsync(connection, transaction, tempTableName, allDataColumnInfos, row);
                }
                catch (DataGenerationException) { throw; }
                catch (Exception ex)
                {
                    var failedCol = DetectFailedColumn(ex, firstPassColumns, row);
                    throw new DataGenerationException(fullName, i, failedCol, ex);
                }

                var keyEntry = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in allDataColumnInfos)
                {
                    if (row.TryGetValue(col.Name, out var v) && v is not null and not DBNull)
                        keyEntry[col.Name] = v;
                }
                _generatedKeys[fullName].Add(keyEntry);

                stagedCount++;
            }

            if (isSelfRef && _generatedKeys[fullName].Count > 1)
            {
                await UpdateSelfReferencesInTempAsync(
                    connection, transaction, tempTableName, table,
                    tablePlan, selfRefColumnNames);
            }

            return new StagingResult(tempTableName, connection, transaction, table, stagedCount, isSelfRef);
        }
        catch
        {
            await transaction.RollbackAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Bootstrap mode: INSERT rows from the temp table into the real table.
    /// Captures identity PK values for FK resolution.
    /// </summary>
    public async Task<int> InsertFromTempTableAsync(StagingResult staging)
    {
        var table = staging.Table;
        var tempTableName = staging.TempTableName;
        var connection = staging.Connection;
        var transaction = staging.Transaction;
        var fullName = table.FullName;

        try
        {
            var dataColumns = table.Columns
                .Where(c => !c.IsAutoGenerated)
                .Where(c => !PlanGenerator.IsUnsupportedType(c))
                .ToList();

            var tempColNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var colCmd = new SqlCommand(
                $"SELECT name FROM tempdb.sys.columns WHERE object_id = OBJECT_ID('tempdb..{tempTableName}') AND name <> 'Id'",
                connection, transaction))
            await using (var colReader = await colCmd.ExecuteReaderAsync())
            {
                while (await colReader.ReadAsync())
                    tempColNames.Add(colReader.GetString(0));
            }

            var insertColumns = dataColumns
                .Where(c => tempColNames.Contains(c.Name))
                .ToList();

            if (table.HasIdentityPk || table.HasSequencePk)
            {
                await InsertFromTempWithOutputAsync(
                    connection, transaction, table, tempTableName, insertColumns);
            }
            else
            {
                await InsertFromTempBulkAsync(
                    connection, transaction, table, tempTableName, insertColumns);

                if (table.PrimaryKeyColumns.Count > 0)
                    BackfillNonIdentityPks(table);
            }

            if (staging.IsSelfRef)
            {
                await ApplySelfReferencesFromTempAsync(
                    connection, transaction, table, tempTableName);
            }

            await using (var cmd = new SqlCommand($"DROP TABLE {tempTableName}", connection, transaction))
                await cmd.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
            return staging.StagedCount;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Update mode: UPDATE existing rows in the real table using generated data from the temp table.
    /// Maps temp rows to existing PK rows round-robin.
    /// </summary>
    public async Task<int> UpdateFromTempTableAsync(StagingResult staging)
    {
        var table = staging.Table;
        var tempTableName = staging.TempTableName;
        var connection = staging.Connection;
        var transaction = staging.Transaction;
        var fullName = table.FullName;

        try
        {
            if (table.PrimaryKeyColumns.Count == 0)
                throw new InvalidOperationException(
                    $"Table [{fullName}] has no primary key. Update mode requires a primary key.");

            var pkColumns = table.Columns.Where(c => c.IsPrimaryKey).ToList();
            var dataColumns = table.Columns
                .Where(c => !c.IsPrimaryKey && !c.IsAutoGenerated)
                .Where(c => !PlanGenerator.IsUnsupportedType(c))
                .ToList();

            var selectPkSql = BuildSelectPkSql(table);
            var originalRows = new List<Dictionary<string, object>>();
            await using (var cmd = new SqlCommand(selectPkSql, connection, transaction))
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var pkValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < pkColumns.Count; i++)
                        pkValues[pkColumns[i].Name] = reader.GetValue(i);
                    originalRows.Add(pkValues);
                }
            }

            if (originalRows.Count == 0)
            {
                await using (var cmd = new SqlCommand($"DROP TABLE {tempTableName}", connection, transaction))
                    await cmd.ExecuteNonQueryAsync();
                await transaction.RollbackAsync();
                await connection.DisposeAsync();
                return 0;
            }

            var mappingTempName = $"#Map_{table.TableName}";
            await CreateMappingTempTableAsync(
                connection, transaction, mappingTempName, pkColumns, dataColumns);

            var tempRowCount = 0;
            await using (var cmd = new SqlCommand($"SELECT COUNT(*) FROM {tempTableName}", connection, transaction))
                tempRowCount = (int)(await cmd.ExecuteScalarAsync() ?? 0);

            for (var i = 0; i < originalRows.Count; i++)
            {
                var pkRow = originalRows[i];
                var tempRowId = (i % tempRowCount) + 1;

                await InsertMappingRowAsync(
                    connection, transaction, mappingTempName, tempTableName,
                    pkColumns, dataColumns, pkRow, tempRowId);
            }

            var updateSql = BuildUpdateFromMappingSql(table, mappingTempName, pkColumns, dataColumns);
            await using (var updateCmd = new SqlCommand(updateSql, connection, transaction))
                await updateCmd.ExecuteNonQueryAsync();

            await using (var cmd = new SqlCommand($"DROP TABLE {mappingTempName}", connection, transaction))
                await cmd.ExecuteNonQueryAsync();
            await using (var cmd = new SqlCommand($"DROP TABLE {tempTableName}", connection, transaction))
                await cmd.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
            return originalRows.Count;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    public record UpdateFkGroup(string RefFullName, string ParentColumn, string ReferencedColumn);

    public record StagingResult(
        string TempTableName,
        SqlConnection Connection,
        SqlTransaction Transaction,
        TableInfo Table,
        int StagedCount,
        bool IsSelfRef) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    #region Staging internals

    private static async Task InsertRowIntoTempAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tempTableName,
        List<ColumnInfo> dataColumns,
        Dictionary<string, object?> row)
    {
        if (dataColumns.Count == 0)
        {
            var sql = $"INSERT INTO {tempTableName} DEFAULT VALUES";
            await using var defaultCmd = new SqlCommand(sql, connection, transaction);
            await defaultCmd.ExecuteNonQueryAsync();
            return;
        }

        var cols = dataColumns.Select(c => $"[{c.Name}]");
        var parms = dataColumns.Select(c => $"@{c.Name}");

        var insertSql = $"INSERT INTO {tempTableName} ({string.Join(", ", cols)}) " +
                        $"VALUES ({string.Join(", ", parms)})";

        await using var cmd = new SqlCommand(insertSql, connection, transaction);

        foreach (var col in dataColumns)
        {
            var paramValue = row.TryGetValue(col.Name, out var v) ? v ?? DBNull.Value : DBNull.Value;
            var param = new SqlParameter($"@{col.Name}", SqlTypeInfo.MapSqlType(col.SqlType)) { Value = paramValue };
            if (param.SqlDbType is SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney)
            {
                param.Precision = col.Precision;
                param.Scale = col.Scale;
            }
            cmd.Parameters.Add(param);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpdateSelfReferencesInTempAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tempTableName,
        TableInfo table,
        TablePlan tablePlan,
        HashSet<string> selfRefColumnNames)
    {
        var rows = _generatedKeys[table.FullName];
        if (rows.Count < 2) return;

        var selfRefFkPlans = tablePlan.Columns
            .Where(c => selfRefColumnNames.Contains(c.Name))
            .ToList();

        var fkPairGroups = selfRefFkPlans
            .GroupBy(c => Helpers.GetArgString(c.GeneratorArgs, "compositeFkGroup"), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Select(c =>
                (ParentColumn: c.Name, ReferencedColumn: Helpers.GetArgString(c.GeneratorArgs, "referencedColumn")))
                .ToList())
            .ToList();

        await ApplySelfRefUpdatesAsync(
            connection, transaction, tempTableName, table.PrimaryKeyColumns,
            rows, fkPairGroups, updateInMemoryRows: true);
    }

    #endregion

    #region Insert from temp (bootstrap)

    private async Task InsertFromTempWithOutputAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TableInfo table,
        string tempTableName,
        List<ColumnInfo> insertColumns)
    {
        var fullName = table.FullName;
        var pkColNames = table.PrimaryKeyColumns;

        var colSelectList = insertColumns.Count > 0
            ? $"[Id], {string.Join(", ", insertColumns.Select(c => $"[{c.Name}]"))}"
            : "[Id]";
        var tempRowsSql = $"SELECT {colSelectList} FROM {tempTableName} ORDER BY [Id]";

        var tempRows = new List<(int Id, Dictionary<string, object?> Row)>();
        await using (var cmd = new SqlCommand(tempRowsSql, connection, transaction))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < insertColumns.Count; i++)
                {
                    var val = reader.GetValue(i + 1);
                    row[insertColumns[i].Name] = val == DBNull.Value ? DBNull.Value : val;
                }
                tempRows.Add((id, row));
            }
        }

        var newKeys = new List<Dictionary<string, object>>();

        foreach (var (tempId, row) in tempRows)
        {
            var sb = new StringBuilder();
            sb.Append($"INSERT INTO [{table.Schema}].[{table.TableName}]");

            if (insertColumns.Count > 0)
            {
                sb.Append(" (");
                sb.Append(string.Join(", ", insertColumns.Select(c => $"[{c.Name}]")));
                sb.Append(')');
            }

            sb.Append(" OUTPUT ");
            sb.Append(string.Join(", ", pkColNames.Select(pk => $"INSERTED.[{pk}]")));

            if (insertColumns.Count > 0)
            {
                sb.Append(" VALUES (");
                sb.Append(string.Join(", ", insertColumns.Select(c => $"@{c.Name}")));
                sb.Append(')');
            }
            else
            {
                sb.Append(" DEFAULT VALUES");
            }

            await using var insertCmd = new SqlCommand(sb.ToString(), connection, transaction);
            foreach (var col in insertColumns)
            {
                var paramValue = row.TryGetValue(col.Name, out var v) ? v ?? DBNull.Value : DBNull.Value;
                var param = new SqlParameter($"@{col.Name}", SqlTypeInfo.MapSqlType(col.SqlType)) { Value = paramValue };
                if (param.SqlDbType is SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney)
                {
                    param.Precision = col.Precision;
                    param.Scale = col.Scale;
                }
                insertCmd.Parameters.Add(param);
            }

            try
            {
                await using var outputReader = await insertCmd.ExecuteReaderAsync();
                if (await outputReader.ReadAsync())
                {
                    var pkValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (var idx = 0; idx < pkColNames.Count; idx++)
                        pkValues[pkColNames[idx]] = outputReader.GetValue(idx);

                    foreach (var col in insertColumns)
                    {
                        if (!pkValues.ContainsKey(col.Name)
                            && row.TryGetValue(col.Name, out var rv)
                            && rv is not null and not DBNull)
                        {
                            pkValues[col.Name] = rv;
                        }
                    }

                    newKeys.Add(pkValues);
                }
            }
            catch (Exception ex) when (IsCheckConstraintViolation(ex))
            {
                throw new DataGenerationException(fullName, tempId - 1, null, ex);
            }
        }

        _generatedKeys[fullName] = newKeys;
    }

    private async Task InsertFromTempBulkAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TableInfo table,
        string tempTableName,
        List<ColumnInfo> insertColumns)
    {
        if (insertColumns.Count == 0)
        {
            var countSql = $"SELECT COUNT(*) FROM {tempTableName}";
            int rowCount;
            await using (var cmd = new SqlCommand(countSql, connection, transaction))
                rowCount = (int)(await cmd.ExecuteScalarAsync() ?? 0);

            for (var i = 0; i < rowCount; i++)
            {
                var sql = $"INSERT INTO [{table.Schema}].[{table.TableName}] DEFAULT VALUES";
                await using var cmd = new SqlCommand(sql, connection, transaction);
                await cmd.ExecuteNonQueryAsync();
            }
            return;
        }

        var colList = string.Join(", ", insertColumns.Select(c => $"[{c.Name}]"));
        var insertSql = $"INSERT INTO [{table.Schema}].[{table.TableName}] ({colList}) " +
                        $"SELECT {colList} FROM {tempTableName} ORDER BY [Id]";

        try
        {
            await using (var cmd = new SqlCommand(insertSql, connection, transaction))
                await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (IsCheckConstraintViolation(ex))
        {
            throw new DataGenerationException(table.FullName, 0, null, ex);
        }
    }

    private void BackfillNonIdentityPks(TableInfo table)
    {
        var fullName = table.FullName;
        if (!_generatedKeys.TryGetValue(fullName, out var keys)) return;

        var pkCols = table.PrimaryKeyColumns;
        var updated = new List<Dictionary<string, object>>();

        foreach (var entry in keys)
        {
            var hasPk = pkCols.All(pk => entry.ContainsKey(pk));
            if (hasPk) updated.Add(entry);
        }

        _generatedKeys[fullName] = updated;
    }

    private async Task ApplySelfReferencesFromTempAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TableInfo table,
        string tempTableName)
    {
        var fullName = table.FullName;
        var rows = _generatedKeys[fullName];
        if (rows.Count < 2) return;

        var selfRefFks = table.ForeignKeys.Where(fk => fk.IsSelfReferencing).ToList();
        if (selfRefFks.Count == 0) return;

        var fkPairGroups = selfRefFks
            .GroupBy(fk => fk.FkName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Select(fk =>
                (ParentColumn: fk.ParentColumn, ReferencedColumn: fk.ReferencedColumn))
                .ToList())
            .ToList();

        var targetTable = $"[{table.Schema}].[{table.TableName}]";
        await ApplySelfRefUpdatesAsync(
            connection, transaction, targetTable, table.PrimaryKeyColumns,
            rows, fkPairGroups, updateInMemoryRows: false);
    }

    private async Task ApplySelfRefUpdatesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string targetTableSql,
        List<string> pkColumns,
        List<Dictionary<string, object>> rows,
        List<List<(string ParentColumn, string ReferencedColumn)>> fkPairGroups,
        bool updateInMemoryRows)
    {
        var rowsToUpdate = rows.Skip(1).Where(_ => _random.NextDouble() < 0.7).ToList();

        foreach (var targetRow in rowsToUpdate)
        {
            foreach (var pairs in fkPairGroups)
            {
                if (!pkColumns.All(pk => targetRow.ContainsKey(pk)))
                    continue;

                bool IsSameRow(Dictionary<string, object> r) =>
                    pkColumns.All(pk =>
                        r.TryGetValue(pk, out var v) &&
                        targetRow.TryGetValue(pk, out var tv) &&
                        Equals(v, tv));

                var candidates = rows.Where(r => !IsSameRow(r)).ToList();
                if (candidates.Count == 0) continue;

                var parentRow = candidates[_random.Next(candidates.Count)];

                var setClauses = pairs.Select((p, i) => $"[{p.ParentColumn}] = @ParentVal{i}");
                var whereClauses = pkColumns.Select((pk, i) => $"[{pk}] = @TargetPk{i}");

                var sql = $"""
                    UPDATE {targetTableSql}
                       SET {string.Join(", ", setClauses)}
                     WHERE {string.Join(" AND ", whereClauses)}
                    """;

                await using var cmd = new SqlCommand(sql, connection, transaction);

                for (var i = 0; i < pairs.Count; i++)
                {
                    cmd.Parameters.AddWithValue($"@ParentVal{i}",
                        parentRow.TryGetValue(pairs[i].ReferencedColumn, out var pv) ? pv : DBNull.Value);
                }

                for (var i = 0; i < pkColumns.Count; i++)
                    cmd.Parameters.AddWithValue($"@TargetPk{i}", targetRow[pkColumns[i]]);

                await cmd.ExecuteNonQueryAsync();

                if (updateInMemoryRows)
                {
                    foreach (var (parentColumn, referencedColumn) in pairs)
                    {
                        if (parentRow.TryGetValue(referencedColumn, out var pv))
                            targetRow[parentColumn] = pv;
                    }
                }
            }
        }
    }

    #endregion

    #region Update from temp

    private static async Task CreateMappingTempTableAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string mappingTempName,
        List<ColumnInfo> pkColumns,
        List<ColumnInfo> dataColumns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {mappingTempName} (");
        sb.AppendLine("    [Id] INT IDENTITY(1,1) PRIMARY KEY,");

        foreach (var pk in pkColumns)
            sb.AppendLine($"    [OriginalId_{pk.Name}] {SqlTypeInfo.FormatSqlColumnType(pk)} NOT NULL,");

        for (var i = 0; i < dataColumns.Count; i++)
        {
            var col = dataColumns[i];
            var trailing = i < dataColumns.Count - 1 ? "," : "";
            sb.AppendLine($"    [{col.Name}] {SqlTypeInfo.FormatSqlColumnType(col)} NULL{trailing}");
        }

        sb.AppendLine(");");

        await using var cmd = new SqlCommand(sb.ToString(), connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertMappingRowAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string mappingTempName,
        string sourceTempName,
        List<ColumnInfo> pkColumns,
        List<ColumnInfo> dataColumns,
        Dictionary<string, object> pkRow,
        int tempRowId)
    {
        var allCols = new List<string>();
        var allValues = new List<string>();

        foreach (var pk in pkColumns)
        {
            allCols.Add($"[OriginalId_{pk.Name}]");
            allValues.Add($"@OriginalId_{pk.Name}");
        }

        foreach (var col in dataColumns)
        {
            allCols.Add($"[{col.Name}]");
            allValues.Add($"(SELECT [{col.Name}] FROM {sourceTempName} WHERE [Id] = @TempRowId)");
        }

        var sql = $"INSERT INTO {mappingTempName} ({string.Join(", ", allCols)}) " +
                  $"VALUES ({string.Join(", ", allValues)})";

        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@TempRowId", tempRowId);

        foreach (var pk in pkColumns)
        {
            var param = new SqlParameter($"@OriginalId_{pk.Name}", SqlTypeInfo.MapSqlType(pk.SqlType))
            {
                Value = pkRow[pk.Name]
            };
            if (param.SqlDbType is SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney)
            {
                param.Precision = pk.Precision;
                param.Scale = pk.Scale;
            }
            cmd.Parameters.Add(param);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private static string BuildUpdateFromMappingSql(
        TableInfo table,
        string mappingTempName,
        List<ColumnInfo> pkColumns,
        List<ColumnInfo> dataColumns)
    {
        if (dataColumns.Count == 0) return "";

        var setClauses = string.Join(",\n           ",
            dataColumns.Select(c => $"t.[{c.Name}] = tmp.[{c.Name}]"));

        var joinClauses = string.Join(" AND ",
            pkColumns.Select(pk => $"t.[{pk.Name}] = tmp.[OriginalId_{pk.Name}]"));

        return $"""
            UPDATE t
               SET {setClauses}
              FROM [{table.Schema}].[{table.TableName}] t
             INNER JOIN {mappingTempName} tmp ON {joinClauses}
            """;
    }

    #endregion

    #region Row building & FK resolution

    private record FkGroup(
        string RefFullName,
        List<(string ParentColumn, string ReferencedColumn, bool IsNullable)> Columns,
        bool IsExternal = false);

    internal record CustomDepGroup(
        string SourceTable,
        string SourceColumn,
        string DependentColumn,
        bool IsNullable);

    private static List<FkGroup> BuildFkGroupsFromPlan<T>(List<T> columns) where T : IColumnMetadata
    {
        var fkColumns = columns
            .OfType<ColumnPlan>()
            .Where(c => c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return fkColumns
            .GroupBy(c => Helpers.GetArgString(c.GeneratorArgs, "compositeFkGroup"), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var refTable = Helpers.GetArgString(first.GeneratorArgs, "referencedTable");
                var isExternal = first.GeneratorArgs.TryGetValue("isExternal", out var ext)
                                 && Helpers.IsTruthy(ext);
                return new FkGroup(
                    refTable,
                    g.Select(c => (c.Name, Helpers.GetArgString(c.GeneratorArgs, "referencedColumn"), c.IsNullable)).ToList(),
                    isExternal);
            })
            .ToList();
    }

    internal static List<CustomDepGroup> BuildCustomDepGroupsFromPlan<T>(List<T> columns) where T : IColumnMetadata
    {
        return columns
            .OfType<ColumnPlan>()
            .Where(c => c.Generator.Equals("customDependency", StringComparison.OrdinalIgnoreCase))
            .Select(c => new CustomDepGroup(
                Helpers.GetArgString(c.GeneratorArgs, "sourceTable"),
                Helpers.GetArgString(c.GeneratorArgs, "sourceColumn"),
                c.Name,
                c.IsNullable))
            .ToList();
    }

    private Dictionary<string, object?> BuildRowFromFkGroups<T>(
        List<T> columns,
        List<FkGroup> fkGroups,
        Func<IColumnMetadata, object> generateValue,
        int sampleSize,
        List<CustomDepGroup>? customDepGroups = null) where T : IColumnMetadata
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var resolvedFkValues = ResolveFkValues(fkGroups, columns, generateValue, sampleSize);
        var resolvedCustomDepValues = ResolveCustomDepValues(customDepGroups);

        foreach (var col in columns)
        {
            if (resolvedFkValues.TryGetValue(col.Name, out var fkValue))
            {
                row[col.Name] = fkValue;
                continue;
            }

            if (resolvedCustomDepValues.TryGetValue(col.Name, out var cdValue))
            {
                row[col.Name] = cdValue;
                continue;
            }

            if (col is ColumnPlan cp && cp.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase))
            {
                row[col.Name] = col.IsNullable ? DBNull.Value : generateValue(col);
                continue;
            }

            if (col is ColumnPlan cp2 && cp2.Generator.Equals("customDependency", StringComparison.OrdinalIgnoreCase))
            {
                row[col.Name] = col.IsNullable ? DBNull.Value : generateValue(col);
                continue;
            }

            if (col.IsNullable && _random.NextDouble() < 0.1)
            {
                row[col.Name] = DBNull.Value;
                continue;
            }

            row[col.Name] = generateValue(col);
        }

        return row;
    }

    internal Dictionary<string, object?> ResolveCustomDepValues(
        List<CustomDepGroup>? customDepGroups)
    {
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (customDepGroups is null or { Count: 0 })
            return resolved;

        foreach (var dep in customDepGroups)
        {
            if (_generatedKeys.TryGetValue(dep.SourceTable, out var sourceRows) && sourceRows.Count > 0)
            {
                var sourceRow = sourceRows[_random.Next(sourceRows.Count)];
                if (sourceRow.TryGetValue(dep.SourceColumn, out var value))
                    resolved[dep.DependentColumn] = value;
            }
        }

        return resolved;
    }

    private Dictionary<string, object?> ResolveFkValues<T>(
        List<FkGroup> fkGroups,
        List<T> columns,
        Func<IColumnMetadata, object> generateValue,
        int sampleSize) where T : IColumnMetadata
    {
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var sortedGroups = SortFkGroupsByTopoDepth(fkGroups);

        // Track all column values from previously picked parent rows so we can
        // constrain later groups. Keyed by the parent row's own column names.
        var pickedColumnValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in sortedGroups)
        {
            var parentRows = GetParentRows(group, sampleSize);

            if (parentRows is { Count: > 0 })
            {
                var filtered = FilterByResolvedValues(parentRows, pickedColumnValues);
                var candidates = filtered.Count > 0 ? filtered : parentRows;
                var parentRow = candidates[_random.Next(candidates.Count)];

                foreach (var (parentColumn, referencedColumn, _) in group.Columns)
                {
                    if (parentRow.TryGetValue(referencedColumn, out var value))
                        resolved[parentColumn] = value;
                }

                foreach (var kvp in parentRow)
                    pickedColumnValues[kvp.Key] = kvp.Value;
            }
            else
            {
                foreach (var (parentColumn, _, isNullable) in group.Columns)
                {
                    var col = columns.FirstOrDefault(c =>
                        c.Name.Equals(parentColumn, StringComparison.OrdinalIgnoreCase));
                    resolved[parentColumn] = (col is { IsNullable: true } || isNullable)
                        ? DBNull.Value
                        : (col != null ? generateValue(col) : DBNull.Value);
                }
            }
        }

        return resolved;
    }

    private List<Dictionary<string, object>>? GetParentRows(FkGroup group, int sampleSize)
    {
        if (_generatedKeys.TryGetValue(group.RefFullName, out var rows) && rows.Count > 0)
            return rows;

        if (group.IsExternal)
        {
            var loaded = LoadExternalFkRows(group, sampleSize);
            return loaded.Count > 0 ? loaded : null;
        }

        return null;
    }

    /// <summary>
    /// Sort FK groups so that groups referencing tables LATER in the generation
    /// order (deeper/more-constrained tables) are resolved first. These rows
    /// contain FK values to ancestor tables, so resolving them first lets us
    /// filter ancestor groups to stay consistent. E.g. resolve the Mid group
    /// first (its rows carry RootId), then filter the Root group to match.
    /// </summary>
    private List<FkGroup> SortFkGroupsByTopoDepth(List<FkGroup> fkGroups)
    {
        if (fkGroups.Count <= 1)
            return fkGroups;

        var keyOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var idx = 0;
        foreach (var key in _generatedKeys.Keys)
            keyOrder[key] = idx++;

        return fkGroups
            .OrderByDescending(g => keyOrder.TryGetValue(g.RefFullName, out var order) ? order : int.MinValue)
            .ToList();
    }

    /// <summary>
    /// Filter candidate parent rows to those consistent with values from
    /// previously picked parent rows. Uses column-name overlap: if a previously
    /// picked row had column "RootId" = 5, and the current candidate rows also
    /// have a "RootId" column, only keep rows where RootId == 5.
    /// </summary>
    private static List<Dictionary<string, object>> FilterByResolvedValues(
        List<Dictionary<string, object>> parentRows,
        Dictionary<string, object> pickedColumnValues)
    {
        if (pickedColumnValues.Count == 0)
            return parentRows;

        var constraints = new List<(string ColumnName, object Value)>();
        foreach (var (colName, val) in pickedColumnValues)
        {
            if (parentRows[0].ContainsKey(colName))
                constraints.Add((colName, val));
        }

        if (constraints.Count == 0)
            return parentRows;

        return parentRows.Where(row =>
            constraints.All(c =>
                row.TryGetValue(c.ColumnName, out var v) && Equals(v, c.Value)))
            .ToList();
    }

    private List<Dictionary<string, object>> LoadExternalFkRows(FkGroup group, int sampleSize)
    {
        if (_generatedKeys.TryGetValue(group.RefFullName, out var cached) && cached.Count > 0)
            return cached;

        var capped = Math.Clamp(sampleSize, 100, 1000);

        var refColumns = group.Columns.Select(c => c.ReferencedColumn).Distinct().ToList();
        var dotIdx = group.RefFullName.IndexOf('.');
        var schema = dotIdx >= 0 ? group.RefFullName[..dotIdx] : "dbo";
        var tableName = dotIdx >= 0 ? group.RefFullName[(dotIdx + 1)..] : group.RefFullName;

        var colList = string.Join(", ", refColumns.Select(c => $"[{c}]"));
        var sql = $"SELECT DISTINCT TOP(@SampleSize) {colList} FROM [{schema}].[{tableName}] ORDER BY NEWID()";

        var rows = new List<Dictionary<string, object>>();

        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@SampleSize", capped);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < refColumns.Count; i++)
            {
                var val = reader.GetValue(i);
                if (val is not DBNull)
                    row[refColumns[i]] = val;
            }
            if (row.Count > 0)
                rows.Add(row);
        }

        _generatedKeys[group.RefFullName] = rows;
        return rows;
    }

    #endregion

    #region Temp table SQL builders

    internal static string BuildCreateTempTableSql(
        string tempTableName,
        List<ColumnInfo> dataColumns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {tempTableName} (");
        sb.Append("    [Id] INT IDENTITY(1,1) PRIMARY KEY");

        foreach (var col in dataColumns)
        {
            sb.AppendLine(",");
            sb.Append($"    [{col.Name}] {SqlTypeInfo.FormatSqlColumnType(col)} NULL");
        }

        sb.AppendLine();
        sb.AppendLine(");");
        return sb.ToString();
    }

    internal static string BuildSelectPkSql(TableInfo table)
    {
        var cols = string.Join(", ", table.PrimaryKeyColumns.Select(pk => $"[{pk}]"));
        return $"SELECT {cols} FROM [{table.Schema}].[{table.TableName}]";
    }

    internal static string BuildUpdateFromTempSql(
        TableInfo table,
        string tempTableName,
        List<ColumnInfo> pkColumns,
        List<ColumnInfo> dataColumns)
    {
        var setClauses = string.Join(",\n           ",
            dataColumns.Select(c => $"t.[{c.Name}] = tmp.[{c.Name}]"));

        var joinClauses = string.Join(" AND ",
            pkColumns.Select(pk => $"t.[{pk.Name}] = tmp.[OriginalId_{pk.Name}]"));

        return $"""
            UPDATE t
               SET {setClauses}
              FROM [{table.Schema}].[{table.TableName}] t
             INNER JOIN {tempTableName} tmp ON {joinClauses}
            """;
    }

    #endregion

    #region Utility methods

    private static ColumnFailureDetail? DetectFailedColumn<T>(
        Exception ex,
        List<T> columns,
        Dictionary<string, object?> row) where T : IColumnMetadata
    {
        var msg = ex.Message;
        foreach (var c in columns)
        {
            if (!msg.Contains(c.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            row.TryGetValue(c.Name, out var val);
            var generator = c is ColumnPlan cp ? cp.Generator : "(auto)";
            return new ColumnFailureDetail
            {
                ColumnName = c.Name,
                SqlType = c.SqlType,
                MaxLength = c.MaxLength,
                Precision = c.Precision,
                Scale = c.Scale,
                Generator = generator,
                GeneratedValueType = val is null or DBNull ? null : val.GetType().Name,
                GeneratedValuePreview = FormatValuePreview(val),
            };
        }
        return null;
    }

    private static bool IsCheckConstraintViolation(Exception ex)
    {
        return ex is SqlException sqlEx && sqlEx.Errors.Cast<SqlError>().Any(e => e.Number == 547);
    }

    private static string? FormatValuePreview(object? value)
    {
        if (value is null or DBNull) return "NULL";
        if (value is byte[] bytes)
            return $"byte[{bytes.Length}]";
        var s = value.ToString() ?? "null";
        return s.Length > 80 ? s[..80] + "..." : s;
    }

    internal static TableInfo TablePlanToTableInfo(TablePlan tablePlan)
    {
        var table = new TableInfo
        {
            Schema = tablePlan.Schema,
            TableName = tablePlan.TableName,
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
                IsRowVersion = cp.IsRowVersion,
                IsUnique = cp.IsUnique,
                IsSequenceDefault = cp.IsSequenceDefault,
                DefaultDefinition = cp.HasDefault ? "(from plan)" : null,
                FullTableName = tablePlan.FullName
            }).ToList(),
            PrimaryKeyColumns = tablePlan.Columns
                .Where(c => c.IsPrimaryKey)
                .Select(c => c.Name)
                .ToList(),
            ForeignKeys = tablePlan.Columns
                .Where(c => c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase))
                .Select(c =>
                {
                    var refTable = Helpers.GetArgString(c.GeneratorArgs, "referencedTable");
                    var dotIdx = refTable.IndexOf('.');
                    return new ForeignKeyInfo
                    {
                        FkName = Helpers.GetArgString(c.GeneratorArgs, "compositeFkGroup"),
                        ParentSchema = tablePlan.Schema,
                        ParentTable = tablePlan.TableName,
                        ParentColumn = c.Name,
                        ReferencedSchema = dotIdx >= 0 ? refTable[..dotIdx] : string.Empty,
                        ReferencedTable = dotIdx >= 0 ? refTable[(dotIdx + 1)..] : refTable,
                        ReferencedColumn = Helpers.GetArgString(c.GeneratorArgs, "referencedColumn"),
                    };
                }).ToList()
        };

        return table;
    }

    private static List<UniqueConstraintInfo> BuildUniqueConstraintsFromPlan(TablePlan tablePlan)
    {
        if (tablePlan.UniqueConstraints is { Count: > 0 })
        {
            return tablePlan.UniqueConstraints.Select(uc => new UniqueConstraintInfo
            {
                Name = uc.Name,
                Columns = new List<string>(uc.Columns),
                FilterDefinition = uc.FilterDefinition
            }).ToList();
        }

        var uniqueColumns = tablePlan.Columns
            .Where(c => c.IsUnique && !c.IsPrimaryKey)
            .ToList();

        return uniqueColumns.Select(c => new UniqueConstraintInfo
        {
            Name = $"UQ_Plan_{c.Name}",
            Columns = [c.Name]
        }).ToList();
    }

    private void InitUniqueConstraintSets(string fullName, List<UniqueConstraintInfo> constraints)
    {
        if (constraints.Count == 0) return;
        if (!_generatedUniqueSets.TryGetValue(fullName, out var sets))
        {
            sets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            _generatedUniqueSets[fullName] = sets;
        }
        foreach (var uc in constraints)
            sets.TryAdd(uc.Name, []);
    }

    private bool TryAddUniqueKeys(
        string fullName,
        List<UniqueConstraintInfo> constraints,
        Dictionary<string, object?> row,
        bool isRetry)
    {
        if (constraints.Count == 0) return true;
        if (!_generatedUniqueSets.TryGetValue(fullName, out var sets)) return true;

        var keysToAdd = new List<(string ConstraintName, string Key)>();

        foreach (var uc in constraints)
        {
            if (!RowSatisfiesFilter(uc, row))
                continue;

            var isFiltered = !string.IsNullOrWhiteSpace(uc.FilterDefinition);
            var key = BuildUniqueKey(uc.Columns, row, isFiltered);
            if (key == null) continue;

            if (!sets.TryGetValue(uc.Name, out var set)) continue;
            if (set.Contains(key))
            {
                if (isRetry)
                {
                    foreach (var (cn, k) in keysToAdd)
                    {
                        if (sets.TryGetValue(cn, out var s))
                            s.Remove(k);
                    }
                }
                return false;
            }
            keysToAdd.Add((uc.Name, key));
        }

        foreach (var (constraintName, key) in keysToAdd)
        {
            if (sets.TryGetValue(constraintName, out var set))
                set.Add(key);
        }

        return true;
    }

    private static string? BuildUniqueKey(
        List<string> columns, Dictionary<string, object?> row, bool isFiltered)
    {
        var parts = new List<string>(columns.Count);
        var hasNull = false;
        foreach (var col in columns)
        {
            if (row.TryGetValue(col, out var val) && val is not null and not DBNull)
                parts.Add(val.ToString()!);
            else
            {
                hasNull = true;
                parts.Add("\0NULL\0");
            }
        }

        if (hasNull && isFiltered)
            return null;

        return string.Join("|", parts);
    }

    internal static bool RowSatisfiesFilter(
        UniqueConstraintInfo constraint,
        Dictionary<string, object?> row)
    {
        if (string.IsNullOrWhiteSpace(constraint.FilterDefinition))
            return true;

        return EvaluateFilterExpression(constraint.FilterDefinition, row);
    }

    private static bool EvaluateFilterExpression(string expr, Dictionary<string, object?> row)
    {
        expr = expr.Trim();

        while (expr.StartsWith('(') && expr.EndsWith(')') && FindMatchingParen(expr, 0) == expr.Length - 1)
            expr = expr[1..^1].Trim();

        var orIndex = FindLogicalOperator(expr, "OR");
        if (orIndex >= 0)
        {
            var left = expr[..orIndex].Trim();
            var right = expr[(orIndex + 2)..].Trim();
            return EvaluateFilterExpression(left, row) || EvaluateFilterExpression(right, row);
        }

        var andIndex = FindLogicalOperator(expr, "AND");
        if (andIndex >= 0)
        {
            var left = expr[..andIndex].Trim();
            var right = expr[(andIndex + 3)..].Trim();
            return EvaluateFilterExpression(left, row) && EvaluateFilterExpression(right, row);
        }

        return EvaluateAtom(expr, row);
    }

    private static readonly Regex IsNullPattern = new(
        @"^\[(?<col>[^\]]+)\]\s+IS\s+NULL$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IsNotNullPattern = new(
        @"^\[(?<col>[^\]]+)\]\s+IS\s+NOT\s+NULL$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EqualityPattern = new(
        @"^\[(?<col>[^\]]+)\]\s*=\s*\(?(?:N)?'(?<val>[^']*)'\)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InequalityPattern = new(
        @"^\[(?<col>[^\]]+)\]\s*<>\s*\(?(?:N)?'(?<val>[^']*)'\)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumericEqualityPattern = new(
        @"^\[(?<col>[^\]]+)\]\s*=\s*\(?(?<val>-?\d+(?:\.\d+)?)\)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumericInequalityPattern = new(
        @"^\[(?<col>[^\]]+)\]\s*<>\s*\(?(?<val>-?\d+(?:\.\d+)?)\)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool EvaluateAtom(string expr, Dictionary<string, object?> row)
    {
        var m = IsNotNullPattern.Match(expr);
        if (m.Success)
        {
            var col = m.Groups["col"].Value;
            return row.TryGetValue(col, out var v) && v is not null and not DBNull;
        }

        m = IsNullPattern.Match(expr);
        if (m.Success)
        {
            var col = m.Groups["col"].Value;
            return !row.TryGetValue(col, out var v) || v is null or DBNull;
        }

        m = EqualityPattern.Match(expr);
        if (m.Success)
        {
            var col = m.Groups["col"].Value;
            var expected = m.Groups["val"].Value;
            if (!row.TryGetValue(col, out var v) || v is null or DBNull)
                return false;
            return string.Equals(v.ToString(), expected, StringComparison.OrdinalIgnoreCase);
        }

        m = InequalityPattern.Match(expr);
        if (m.Success)
        {
            var col = m.Groups["col"].Value;
            var expected = m.Groups["val"].Value;
            if (!row.TryGetValue(col, out var v) || v is null or DBNull)
                return true;
            return !string.Equals(v.ToString(), expected, StringComparison.OrdinalIgnoreCase);
        }

        m = NumericEqualityPattern.Match(expr);
        if (m.Success)
        {
            var col = m.Groups["col"].Value;
            var expected = m.Groups["val"].Value;
            if (!row.TryGetValue(col, out var v) || v is null or DBNull)
                return false;
            return string.Equals(v.ToString(), expected, StringComparison.OrdinalIgnoreCase);
        }

        m = NumericInequalityPattern.Match(expr);
        if (m.Success)
        {
            var col = m.Groups["col"].Value;
            var expected = m.Groups["val"].Value;
            if (!row.TryGetValue(col, out var v) || v is null or DBNull)
                return true;
            return !string.Equals(v.ToString(), expected, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static int FindMatchingParen(string expr, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            else if (expr[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static int FindLogicalOperator(string expr, string op)
    {
        var depth = 0;
        var opLen = op.Length;
        for (var i = 0; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            else if (expr[i] == ')') depth--;
            else if (depth == 0
                     && i + opLen <= expr.Length
                     && (i == 0 || char.IsWhiteSpace(expr[i - 1]))
                     && string.Equals(expr.Substring(i, opLen), op, StringComparison.OrdinalIgnoreCase)
                     && (i + opLen >= expr.Length || char.IsWhiteSpace(expr[i + opLen])))
            {
                return i;
            }
        }
        return -1;
    }


    private static string? BuildPkKeyFromRow(TableInfo table, Dictionary<string, object?> row)
    {
        if (table.PrimaryKeyColumns.Count == 0)
            return null;

        var parts = new List<string>(table.PrimaryKeyColumns.Count);
        foreach (var pk in table.PrimaryKeyColumns)
        {
            if (row.TryGetValue(pk, out var val) && val is not null and not DBNull)
                parts.Add(val.ToString()!);
            else
                return null;
        }

        return parts.Count == table.PrimaryKeyColumns.Count
            ? string.Join("|", parts)
            : null;
    }


    #endregion
}
