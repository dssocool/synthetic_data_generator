using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class DataInserter : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ColumnValueGenerator _valueGen;
    private readonly IReadOnlySet<string> _selfReferencingTables;
    private readonly int _externalSourceBufferSize;
    private readonly string? _planBasePath;

    // All cross-table state uses ConcurrentDictionary so the executor can run
    // unrelated tables in parallel. Within a single table's generation, only
    // one task ever writes to that table's slot (the scheduler waits for all
    // parents to finish before dispatching dependents), so the values stored
    // here (Lists / HashSets / inner Dictionaries) are not themselves
    // concurrently mutated.
    internal readonly ConcurrentDictionary<string, List<Dictionary<string, object>>> _generatedKeys = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _generatedPkSets = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, HashSet<string>>> _generatedUniqueSets = new();

    // Tracks the order in which table keys were first added to _generatedKeys.
    // Required because ConcurrentDictionary does NOT preserve insertion order
    // (unlike Dictionary<TKey, TValue>), and SortFkGroupsByTopoDepth uses this
    // order as a proxy for topological depth: groups whose referenced tables
    // were generated *later* are resolved first so their FK chains can
    // constrain ancestor groups (this is what keeps the diamond consistent in
    // tests like Test72_DeepChainDiamond).
    private readonly List<string> _generationOrder = new();
    private readonly object _generationOrderLock = new();

    // Maps (table, column) -> (referencedTable, referencedColumn) for FK columns
    // of tables that have already been generated. Populated during staging.
    private readonly ConcurrentDictionary<(string Table, string Column), (string RefTable, string RefColumn)>
        _fkColumnMap = new(FullNameColumnComparer.Instance);

    // One streamer per (externalTable, column) — shared across all dependents
    // that pull from the same root, so we never open more than one cursor per
    // external source even if multiple groups reference it. Pick() is
    // internally locked so concurrent table tasks are safe.
    private readonly ConcurrentDictionary<(string Table, string Column), ExternalSourceStreamer>
        _externalSourceStreamers = new(FullNameColumnComparer.Instance);

    // One value-list picker per (externalTable, column) — shared across all
    // dependents that pull from the same CustomValueLists-backed root, so each
    // file is loaded into memory at most once per run. Pick() is internally
    // locked so concurrent table tasks are safe.
    private readonly ConcurrentDictionary<(string Table, string Column), ValueListSource>
        _valueListSources = new(FullNameColumnComparer.Instance);

    private const int MaxPkRetries = 100;
    private const int DefaultExternalSourceBufferSize = 10_000;
    internal const int MaxInsertedPkDisplayCount = 10_000;

    public DataInserter(
        string connectionString,
        ColumnValueGenerator valueGen,
        IReadOnlySet<string> selfReferencingTables,
        int externalSourceBufferSize = DefaultExternalSourceBufferSize,
        string? planBasePath = null)
    {
        _connectionString = connectionString;
        _valueGen = valueGen;
        _selfReferencingTables = selfReferencingTables;
        _externalSourceBufferSize = externalSourceBufferSize > 0
            ? externalSourceBufferSize
            : DefaultExternalSourceBufferSize;
        _planBasePath = planBasePath;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var streamer in _externalSourceStreamers.Values)
            await streamer.DisposeAsync();
        _externalSourceStreamers.Clear();
    }

    /// <summary>
    /// Returns display strings for primary keys of rows inserted for <paramref name="table"/>.
    /// Call immediately after <see cref="InsertGeneratedRowsAsync"/> for that table.
    /// Returns null when the table has no primary key.
    /// </summary>
    public IReadOnlyList<string>? GetInsertedPkDisplayValues(TableInfo table, int maxCount = MaxInsertedPkDisplayCount)
    {
        if (table.PrimaryKeyColumns.Count == 0)
            return null;

        if (!_generatedKeys.TryGetValue(table.FullName, out var keys) || keys.Count == 0)
            return [];

        var pkCols = table.PrimaryKeyColumns;
        var result = new List<string>(Math.Min(keys.Count, maxCount));

        foreach (var row in keys)
        {
            if (result.Count >= maxCount)
                break;

            if (!pkCols.All(pk => row.TryGetValue(pk, out var v) && v is not null and not DBNull))
                continue;

            result.Add(FormatPkDisplayValue(pkCols, row));
        }

        return result;
    }

    internal static string FormatPkDisplayValue(IReadOnlyList<string> pkColumns, IReadOnlyDictionary<string, object> row)
    {
        if (pkColumns.Count == 1)
            return FormatPkScalar(row[pkColumns[0]]);

        return string.Join(", ", pkColumns.Select(pk => $"{pk}={FormatPkScalar(row[pk])}"));
    }

    private static string FormatPkScalar(object value) =>
        value switch
        {
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            DateTime dt => dt.ToString("O"),
            DateTimeOffset dto => dto.ToString("O"),
            _ => value.ToString() ?? "null"
        };

    /// <summary>
    /// Generate rows in memory and return a preparation result.
    /// No database connection is opened; the caller decides the insert strategy.
    /// When <paramref name="valueGen"/> is provided, that generator (typically
    /// seeded per-table for deterministic parallel runs) is used instead of the
    /// inserter's shared <see cref="_valueGen"/>. Bogus's <c>Faker</c> is not
    /// thread-safe, so parallel callers MUST pass distinct instances.
    /// </summary>
    public GenerationResult GenerateRows(TablePlan tablePlan, ColumnValueGenerator? valueGen = null)
    {
        var gen = valueGen ?? _valueGen;
        var table = TablePlanToTableInfo(tablePlan);
        var fullName = tablePlan.FullName;

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

        foreach (var group in fkGroups)
            foreach (var (parentCol, refCol, _) in group.Columns)
                _fkColumnMap[(fullName, parentCol)] = (group.RefFullName, refCol);

        if (_generatedKeys.TryAdd(fullName, []))
            RecordGenerationOrder(fullName);
        _generatedPkSets.TryAdd(fullName, []);
        InitUniqueConstraintSets(fullName, uniqueConstraints);

        var dataTable = new DataTable();
        dataTable.Columns.Add("Id", typeof(int));
        foreach (var col in allDataColumnInfos)
            dataTable.Columns.Add(col.Name, typeof(object));

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
                        col => gen.GenerateFromPlan((ColumnPlan)col) ?? DBNull.Value,
                        tablePlan.RowCount,
                        customDepGroups);
                    pkKey = BuildPkKeyFromRow(table, row);
                    uniqueOk = TryAddUniqueKeys(fullName, uniqueConstraints, row, attempt > 0);
                    attempt++;
                } while ((!uniqueOk || (pkKey != null
                                        && !_generatedPkSets[fullName].Add(pkKey)))
                         && attempt < MaxPkRetries);

                if (attempt >= MaxPkRetries)
                {
                    var narrowCols = tablePlan.Columns
                        .Where(c => (c.IsPrimaryKey || c.IsUnique)
                                    && !c.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase))
                        .Select(c => $"{c.Name} ({c.SqlType}({c.MaxLength}))")
                        .ToList();
                    var colDetail = narrowCols.Count > 0
                        ? $" Narrow unique/PK columns: {string.Join(", ", narrowCols)}."
                        : "";
                    throw new InvalidOperationException(
                        $"Could not generate unique values for [{fullName}] row {i + 1}/{tablePlan.RowCount} " +
                        $"after {MaxPkRetries} attempts.{colDetail} " +
                        $"Consider reducing rowCount for this table in the plan file, " +
                        $"or using a wider value range.");
                }
            }
            catch (DataGenerationException) { throw; }
            catch (Exception ex)
            {
                throw new DataGenerationException(fullName, i, null, ex);
            }

            var dtRow = dataTable.NewRow();
            dtRow["Id"] = i + 1;
            foreach (var col in allDataColumnInfos)
                dtRow[col.Name] = row.TryGetValue(col.Name, out var v) ? v ?? DBNull.Value : DBNull.Value;
            dataTable.Rows.Add(dtRow);

            var keyEntry = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in allDataColumnInfos)
            {
                if (row.TryGetValue(col.Name, out var v) && v is not null and not DBNull)
                    keyEntry[col.Name] = v;
            }
            _generatedKeys[fullName].Add(keyEntry);
        }

        return new GenerationResult(table, dataTable, allDataColumnInfos, tablePlan, isSelfRef, selfRefColumnNames);
    }

    /// <summary>
    /// Insert mode: insert generated rows into the target table.
    /// For non-identity, non-self-ref tables, SqlBulkCopy goes directly to the target.
    /// For identity/sequence PK or self-ref tables, stages via a temp table first.
    /// When <paramref name="sharedConnection"/> is provided, it is reused and NOT disposed
    /// by this method; the caller owns its lifetime.
    /// </summary>
    public async Task<int> InsertGeneratedRowsAsync(
        GenerationResult gen, SqlConnection? sharedConnection = null)
    {
        var table = gen.Table;
        var fullName = table.FullName;
        var needsTempTable = table.HasIdentityPk || table.HasSequencePk || gen.IsSelfRef;

        var ownsConnection = sharedConnection is null;
        var connection = sharedConnection ?? new SqlConnection(_connectionString);
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            if (needsTempTable)
            {
                var tempTableName = $"#{table.TableName}";
                var createSql = BuildCreateTempTableSql(tempTableName, gen.DataColumnInfos);
                await using (var cmd = new SqlCommand(createSql, connection, transaction))
                    await cmd.ExecuteNonQueryAsync();

                await BulkCopyAsync(connection, transaction, tempTableName, gen.DataTable);

                if (gen.IsSelfRef && _generatedKeys[fullName].Count > 1)
                {
                    await UpdateSelfReferencesInTempAsync(
                        connection, transaction, tempTableName, table,
                        gen.TablePlan, gen.SelfRefColumnNames);
                }

                var insertColumns = gen.DataColumnInfos
                    .Where(c => !c.IsAutoGenerated)
                    .Where(c => !PlanGenerator.IsUnsupportedType(c))
                    .ToList();

                if (table.HasIdentityPk || table.HasSequencePk)
                {
                    await InsertFromTempWithMergeOutputAsync(
                        connection, transaction, table, tempTableName, insertColumns);
                }
                else
                {
                    await InsertFromTempBulkAsync(
                        connection, transaction, table, tempTableName, insertColumns);

                    if (table.PrimaryKeyColumns.Count > 0)
                        BackfillNonIdentityPks(table);
                }

                if (gen.IsSelfRef)
                {
                    await ApplySelfReferencesFromTempAsync(
                        connection, transaction, table, tempTableName);
                }

                await using (var cmd = new SqlCommand($"DROP TABLE {tempTableName}", connection, transaction))
                    await cmd.ExecuteNonQueryAsync();
            }
            else
            {
                var insertColumns = gen.DataColumnInfos
                    .Where(c => !c.IsAutoGenerated)
                    .Where(c => !PlanGenerator.IsUnsupportedType(c))
                    .ToList();

                if (insertColumns.Count == 0)
                {
                    for (var i = 0; i < gen.DataTable.Rows.Count; i++)
                    {
                        var sql = $"INSERT INTO {table.BracketedName} DEFAULT VALUES";
                        await using var cmd = new SqlCommand(sql, connection, transaction);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                else
                {
                    await DirectBulkCopyAsync(connection, transaction, table, gen.DataTable, insertColumns);
                }

                if (table.PrimaryKeyColumns.Count > 0)
                    BackfillNonIdentityPks(table);
            }

            await transaction.CommitAsync();
            return gen.DataTable.Rows.Count;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            if (ownsConnection)
                await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Stage generated data into a temp table. Returns (tempTableName, connection, transaction, table, stagedCount).
    /// The caller must call InsertFromTempTableAsync or UpdateFromTempTableAsync, then commit/rollback.
    /// When <paramref name="sharedConnection"/> is provided, it is reused and the returned
    /// <see cref="StagingResult"/> will NOT own (dispose) the connection.
    /// </summary>
    public async Task<StagingResult> StageToTempTableAsync(
        TablePlan tablePlan,
        SqlConnection? sharedConnection = null,
        ColumnValueGenerator? valueGen = null)
    {
        var gen = GenerateRows(tablePlan, valueGen);
        var tempTableName = $"#{gen.Table.TableName}";

        var ownsConnection = sharedConnection is null;
        var connection = sharedConnection ?? new SqlConnection(_connectionString);
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            var createSql = BuildCreateTempTableSql(tempTableName, gen.DataColumnInfos);
            await using (var cmd = new SqlCommand(createSql, connection, transaction))
                await cmd.ExecuteNonQueryAsync();

            await BulkCopyAsync(connection, transaction, tempTableName, gen.DataTable);

            if (gen.IsSelfRef && _generatedKeys[gen.Table.FullName].Count > 1)
            {
                await UpdateSelfReferencesInTempAsync(
                    connection, transaction, tempTableName, gen.Table,
                    gen.TablePlan, gen.SelfRefColumnNames);
            }

            return new StagingResult(tempTableName, connection, transaction, gen.Table,
                gen.DataTable.Rows.Count, gen.IsSelfRef, ownsConnection);
        }
        catch
        {
            await transaction.RollbackAsync();
            if (ownsConnection)
                await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Insert mode: INSERT rows from the temp table into the real table.
    /// Captures identity PK values for FK resolution.
    /// Used only by update mode's staging flow now.
    /// </summary>
    public async Task<int> InsertFromTempTableAsync(StagingResult staging)
    {
        var table = staging.Table;
        var tempTableName = staging.TempTableName;
        var connection = staging.Connection;
        var transaction = staging.Transaction;

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
                await InsertFromTempWithMergeOutputAsync(
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
            if (staging.OwnsConnection)
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
                if (staging.OwnsConnection)
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
            if (staging.OwnsConnection)
                await connection.DisposeAsync();
        }
    }

    public record UpdateFkGroup(string RefFullName, string ParentColumn, string ReferencedColumn);

    public record GenerationResult(
        TableInfo Table,
        DataTable DataTable,
        List<ColumnInfo> DataColumnInfos,
        TablePlan TablePlan,
        bool IsSelfRef,
        HashSet<string> SelfRefColumnNames);

    public record StagingResult(
        string TempTableName,
        SqlConnection Connection,
        SqlTransaction Transaction,
        TableInfo Table,
        int StagedCount,
        bool IsSelfRef,
        bool OwnsConnection = true) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            if (OwnsConnection)
                await Connection.DisposeAsync();
        }
    }

    #region Staging internals

    private static async Task BulkCopyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string destinationTable,
        DataTable dataTable)
    {
        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = destinationTable,
            BulkCopyTimeout = 0
        };
        foreach (DataColumn col in dataTable.Columns)
            bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulkCopy.WriteToServerAsync(dataTable);
    }

    private static async Task DirectBulkCopyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TableInfo table,
        DataTable dataTable,
        List<ColumnInfo> insertColumns)
    {
        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = $"{table.BracketedName}",
            BulkCopyTimeout = 0
        };
        foreach (var col in insertColumns)
            bulkCopy.ColumnMappings.Add(col.Name, col.Name);
        await bulkCopy.WriteToServerAsync(dataTable);
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

    #region Insert from temp (insert)

    /// <summary>
    /// Uses MERGE...OUTPUT to insert all rows from the temp table into the target
    /// in a single statement, capturing (tempId, identityPk) pairs for FK resolution.
    /// </summary>
    private async Task InsertFromTempWithMergeOutputAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TableInfo table,
        string tempTableName,
        List<ColumnInfo> insertColumns)
    {
        var fullName = table.FullName;
        var pkColNames = table.PrimaryKeyColumns;

        if (insertColumns.Count == 0)
        {
            var newKeys = new List<Dictionary<string, object>>();
            var countSql = $"SELECT COUNT(*) FROM {tempTableName}";
            int rowCount;
            await using (var cmd = new SqlCommand(countSql, connection, transaction))
                rowCount = (int)(await cmd.ExecuteScalarAsync() ?? 0);

            for (var i = 0; i < rowCount; i++)
            {
                var sb = new StringBuilder();
                sb.Append($"INSERT INTO {table.BracketedName}");
                sb.Append(" OUTPUT ");
                sb.Append(string.Join(", ", pkColNames.Select(pk => $"INSERTED.[{pk}]")));
                sb.Append(" DEFAULT VALUES");

                await using var insertCmd = new SqlCommand(sb.ToString(), connection, transaction);
                await using var outputReader = await insertCmd.ExecuteReaderAsync();
                if (await outputReader.ReadAsync())
                {
                    var pkValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (var idx = 0; idx < pkColNames.Count; idx++)
                        pkValues[pkColNames[idx]] = outputReader.GetValue(idx);
                    newKeys.Add(pkValues);
                }
            }
            _generatedKeys[fullName] = newKeys;
            return;
        }

        var srcColList = string.Join(", ", insertColumns.Select(c => $"src.[{c.Name}]"));
        var insertColList = string.Join(", ", insertColumns.Select(c => $"[{c.Name}]"));

        var outputCols = new List<string> { "src.[Id]" };
        outputCols.AddRange(pkColNames.Select(pk => $"INSERTED.[{pk}]"));
        outputCols.AddRange(insertColumns
            .Where(c => !pkColNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            .Select(c => $"INSERTED.[{c.Name}]"));

        var mergeSql = $"""
            MERGE INTO {table.BracketedName} AS tgt
            USING (SELECT * FROM {tempTableName}) AS src
            ON 1 = 0
            WHEN NOT MATCHED THEN
              INSERT ({insertColList}) VALUES ({srcColList})
            OUTPUT {string.Join(", ", outputCols)};
            """;

        var newMergeKeys = new List<(int TempId, Dictionary<string, object> Row)>();
        try
        {
            await using var mergeCmd = new SqlCommand(mergeSql, connection, transaction);
            await using var reader = await mergeCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var tempId = reader.GetInt32(0);
                var pkValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (var idx = 0; idx < pkColNames.Count; idx++)
                    pkValues[pkColNames[idx]] = reader.GetValue(idx + 1);

                var colOffset = 1 + pkColNames.Count;
                var nonPkInsertCols = insertColumns
                    .Where(c => !pkColNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                for (var idx = 0; idx < nonPkInsertCols.Count; idx++)
                {
                    var val = reader.GetValue(colOffset + idx);
                    if (val is not DBNull)
                        pkValues[nonPkInsertCols[idx].Name] = val;
                }

                newMergeKeys.Add((tempId, pkValues));
            }
        }
        catch (Exception ex) when (IsCheckConstraintViolation(ex))
        {
            throw new DataGenerationException(fullName, 0, null, ex);
        }

        _generatedKeys[fullName] = newMergeKeys
            .OrderBy(x => x.TempId)
            .Select(x => x.Row)
            .ToList();
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
                var sql = $"INSERT INTO {table.BracketedName} DEFAULT VALUES";
                await using var cmd = new SqlCommand(sql, connection, transaction);
                await cmd.ExecuteNonQueryAsync();
            }
            return;
        }

        var colList = string.Join(", ", insertColumns.Select(c => $"[{c.Name}]"));
        var insertSql = $"INSERT INTO {table.BracketedName} ({colList}) " +
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

        var targetTable = $"{table.BracketedName}";
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
        var rowsToUpdate = rows.Skip(1).Where(_ => Random.Shared.NextDouble() < 0.7).ToList();

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

                var parentRow = candidates[Random.Shared.Next(candidates.Count)];

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
              FROM {table.BracketedName} t
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
        bool IsNullable,
        bool IsExternal = false,
        string? ValuesFile = null,
        IReadOnlyList<string>? Values = null);

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
            .Select(c =>
            {
                var valuesFile = c.GeneratorArgs.TryGetValue("valuesFile", out var vf)
                    ? vf as string
                    : null;

                IReadOnlyList<string>? values = null;
                if (c.GeneratorArgs.TryGetValue("values", out var v) && v is not null)
                {
                    values = v switch
                    {
                        IEnumerable<string> ss => ss.ToList(),
                        IEnumerable<object?> os => os.Where(o => o is not null)
                                                     .Select(o => o!.ToString()!)
                                                     .ToList(),
                        _ => null
                    };
                    if (values is { Count: 0 })
                        values = null;
                }

                return new CustomDepGroup(
                    Helpers.GetArgString(c.GeneratorArgs, "sourceTable"),
                    Helpers.GetArgString(c.GeneratorArgs, "sourceColumn"),
                    c.Name,
                    c.IsNullable,
                    c.GeneratorArgs.TryGetValue("isExternal", out var ext) && Helpers.IsTruthy(ext),
                    string.IsNullOrEmpty(valuesFile) ? null : valuesFile,
                    values);
            })
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

            if (col.IsNullable && Random.Shared.NextDouble() < 0.1)
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
            if (!string.IsNullOrEmpty(dep.ValuesFile)
                || dep.Values is { Count: > 0 })
            {
                var picker = GetOrCreateValueListSource(dep);
                resolved[dep.DependentColumn] = picker.Pick();
                continue;
            }

            if (dep.IsExternal)
            {
                var streamer = GetOrCreateExternalSourceStreamer(dep.SourceTable, dep.SourceColumn);
                resolved[dep.DependentColumn] = streamer.Pick();
                continue;
            }

            if (_generatedKeys.TryGetValue(dep.SourceTable, out var sourceRows) && sourceRows.Count > 0)
            {
                var sourceRow = sourceRows[Random.Shared.Next(sourceRows.Count)];
                if (sourceRow.TryGetValue(dep.SourceColumn, out var value))
                    resolved[dep.DependentColumn] = value;
            }
        }

        return resolved;
    }

    private ExternalSourceStreamer GetOrCreateExternalSourceStreamer(string sourceTable, string sourceColumn)
    {
        var key = (sourceTable, sourceColumn);
        return _externalSourceStreamers.GetOrAdd(key, k =>
            new ExternalSourceStreamer(
                _connectionString, k.Table, k.Column, _externalSourceBufferSize, Random.Shared));
    }

    private ValueListSource GetOrCreateValueListSource(CustomDepGroup dep)
    {
        var key = (dep.SourceTable, dep.SourceColumn);
        return _valueListSources.GetOrAdd(key, _ =>
        {
            if (!string.IsNullOrEmpty(dep.ValuesFile))
            {
                var resolvedPath = Path.IsPathRooted(dep.ValuesFile)
                    ? dep.ValuesFile
                    : Path.GetFullPath(dep.ValuesFile, _planBasePath ?? Directory.GetCurrentDirectory());
                return new ValueListSource(resolvedPath, Random.Shared);
            }
            return new ValueListSource(dep.Values!, Random.Shared);
        });
    }

    private Dictionary<string, object?> ResolveFkValues<T>(
        List<FkGroup> fkGroups,
        List<T> columns,
        Func<IColumnMetadata, object> generateValue,
        int sampleSize) where T : IColumnMetadata
    {
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (fkGroups.Count <= 1)
        {
            foreach (var group in fkGroups)
                ResolveGroupSimple(group, columns, generateValue, sampleSize, resolved);
            return resolved;
        }

        var sortedGroups = SortFkGroupsByTopoDepth(fkGroups);

        // Pre-compute shared ancestor constraints: find ancestor (table, column)
        // pairs that multiple groups reference (directly or transitively via
        // _fkColumnMap). Intersect the valid values to pick a common ancestor value.
        var sharedAncestorValues = ComputeSharedAncestorValues(sortedGroups, sampleSize);

        var resolvedRefs = new Dictionary<(string Table, string Column), object>(
            FullNameColumnComparer.Instance);

        foreach (var (key, val) in sharedAncestorValues)
            resolvedRefs[key] = val;

        foreach (var group in sortedGroups)
        {
            var parentRows = GetParentRows(group, sampleSize);

            if (parentRows is { Count: > 0 })
            {
                var filtered = FilterByResolvedRefs(parentRows, group, resolvedRefs, _fkColumnMap);
                var candidates = filtered.Count > 0 ? filtered : parentRows;
                var parentRow = candidates[Random.Shared.Next(candidates.Count)];

                foreach (var (parentColumn, referencedColumn, _) in group.Columns)
                {
                    if (parentRow.TryGetValue(referencedColumn, out var value))
                    {
                        resolved[parentColumn] = value;
                        resolvedRefs.TryAdd((group.RefFullName, referencedColumn), value);
                    }
                }

                ExpandAncestorRefs(parentRow, group.RefFullName, resolvedRefs);
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

    private void ResolveGroupSimple<T>(
        FkGroup group,
        List<T> columns,
        Func<IColumnMetadata, object> generateValue,
        int sampleSize,
        Dictionary<string, object?> resolved) where T : IColumnMetadata
    {
        var parentRows = GetParentRows(group, sampleSize);
        if (parentRows is { Count: > 0 })
        {
            var parentRow = parentRows[Random.Shared.Next(parentRows.Count)];
            foreach (var (parentColumn, referencedColumn, _) in group.Columns)
            {
                if (parentRow.TryGetValue(referencedColumn, out var value))
                    resolved[parentColumn] = value;
            }
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

    /// <summary>
    /// For groups that share a common ancestor (via FK chains), find ancestor
    /// (table, column) values that are valid across ALL groups referencing that
    /// ancestor. Picks one random value from the intersection.
    /// </summary>
    private Dictionary<(string Table, string Column), object> ComputeSharedAncestorValues(
        List<FkGroup> groups, int sampleSize)
    {
        var result = new Dictionary<(string Table, string Column), object>(
            FullNameColumnComparer.Instance);

        // Map each group to the set of ancestor (table, column) pairs it can reach
        var groupAncestors = new List<Dictionary<(string, string), HashSet<object>>>();

        foreach (var group in groups)
        {
            var ancestors = new Dictionary<(string, string), HashSet<object>>(
                FullNameColumnComparer.Instance);
            var parentRows = GetParentRows(group, sampleSize);
            if (parentRows is not { Count: > 0 })
            {
                groupAncestors.Add(ancestors);
                continue;
            }

            // Direct: the group references (RefFullName, referencedColumn)
            foreach (var (_, refCol, _) in group.Columns)
            {
                var key = (group.RefFullName, refCol);
                if (!ancestors.TryGetValue(key, out var set))
                {
                    set = new HashSet<object>();
                    ancestors[key] = set;
                }
                foreach (var row in parentRows)
                {
                    if (row.TryGetValue(refCol, out var v) && v is not DBNull)
                        set.Add(v);
                }
            }

            // Transitive: follow FK chains from the referenced table upward
            foreach (var row in parentRows)
            {
                foreach (var (colName, val) in row)
                {
                    if (val is DBNull) continue;
                    if (!_fkColumnMap.TryGetValue((group.RefFullName, colName), out var target))
                        continue;

                    var key = (target.RefTable, target.RefColumn);
                    if (!ancestors.TryGetValue(key, out var set))
                    {
                        set = new HashSet<object>();
                        ancestors[key] = set;
                    }
                    set.Add(val);
                }
            }

            groupAncestors.Add(ancestors);
        }

        // Find ancestor keys shared by 2+ groups, intersect their value sets
        var allKeys = new Dictionary<(string, string), List<int>>(
            FullNameColumnComparer.Instance);

        for (var i = 0; i < groupAncestors.Count; i++)
        {
            foreach (var key in groupAncestors[i].Keys)
            {
                if (!allKeys.TryGetValue(key, out var indices))
                {
                    indices = [];
                    allKeys[key] = indices;
                }
                indices.Add(i);
            }
        }

        foreach (var (key, indices) in allKeys)
        {
            if (indices.Count < 2)
                continue;

            HashSet<object>? intersection = null;
            foreach (var idx in indices)
            {
                var set = groupAncestors[idx][key];
                if (intersection == null)
                    intersection = new HashSet<object>(set);
                else
                    intersection.IntersectWith(set);
            }

            if (intersection is { Count: > 0 })
            {
                var pick = intersection.ElementAt(Random.Shared.Next(intersection.Count));
                result[key] = pick;
            }
        }

        return result;
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
    /// filter ancestor groups to stay consistent.
    /// </summary>
    private List<FkGroup> SortFkGroupsByTopoDepth(List<FkGroup> fkGroups)
    {
        if (fkGroups.Count <= 1)
            return fkGroups;

        // Snapshot _generationOrder under the lock; we cannot rely on
        // ConcurrentDictionary.Keys for insertion order (it's a hash-bucket
        // enumeration, not LRU/insertion-ordered). The list itself is purely
        // append-only so a defensive copy is enough — we do NOT need to hold
        // the lock for the OrderByDescending below.
        List<string> orderSnapshot;
        lock (_generationOrderLock)
            orderSnapshot = [.._generationOrder];

        var keyOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < orderSnapshot.Count; i++)
            keyOrder[orderSnapshot[i]] = i;

        return fkGroups
            .OrderByDescending(g => keyOrder.TryGetValue(g.RefFullName, out var order) ? order : int.MinValue)
            .ToList();
    }

    /// <summary>
    /// After picking a row from a referenced table, trace its FK column values
    /// upward through the ancestor chain using _fkColumnMap and record every
    /// (table, column) -> value we can resolve. Handles renamed columns and
    /// transitive diamonds (e.g. Leaf -> Mid2 -> Mid1 -> Root).
    /// </summary>
    private void ExpandAncestorRefs(
        Dictionary<string, object> pickedRow,
        string pickedTableFullName,
        Dictionary<(string Table, string Column), object> resolvedRefs)
    {
        var queue = new Queue<(string TableName, Dictionary<string, object> Row)>();
        queue.Enqueue((pickedTableFullName, pickedRow));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pickedTableFullName };

        while (queue.Count > 0)
        {
            var (tableName, row) = queue.Dequeue();

            foreach (var (colName, val) in row)
            {
                if (val is DBNull)
                    continue;

                if (!_fkColumnMap.TryGetValue((tableName, colName), out var fkTarget))
                    continue;

                resolvedRefs.TryAdd((fkTarget.RefTable, fkTarget.RefColumn), val);

                if (visited.Contains(fkTarget.RefTable))
                    continue;

                if (!_generatedKeys.TryGetValue(fkTarget.RefTable, out var ancestorRows)
                    || ancestorRows.Count == 0)
                    continue;

                var matchedAncestor = ancestorRows.FirstOrDefault(ar =>
                    ar.TryGetValue(fkTarget.RefColumn, out var v) && Equals(v, val));

                if (matchedAncestor == null)
                    continue;

                foreach (var (ancCol, ancVal) in matchedAncestor)
                {
                    if (ancVal is not DBNull)
                        resolvedRefs.TryAdd((fkTarget.RefTable, ancCol), ancVal);
                }

                visited.Add(fkTarget.RefTable);
                queue.Enqueue((fkTarget.RefTable, matchedAncestor));
            }
        }
    }

    /// <summary>
    /// Filter candidate parent rows to those consistent with previously resolved
    /// reference values. Two matching strategies:
    /// 1. Direct: if resolvedRefs has (group.RefFullName, column), filter by that column.
    /// 2. Transitive: if a candidate row column is a FK to some (refTable, refColumn)
    ///    that's already resolved, filter by matching that FK value.
    /// </summary>
    private static List<Dictionary<string, object>> FilterByResolvedRefs(
        List<Dictionary<string, object>> parentRows,
        FkGroup group,
        Dictionary<(string Table, string Column), object> resolvedRefs,
        IReadOnlyDictionary<(string Table, string Column), (string RefTable, string RefColumn)> fkColumnMap)
    {
        if (resolvedRefs.Count == 0)
            return parentRows;

        var constraints = new List<(string ColumnName, object Value)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ((table, column), val) in resolvedRefs)
        {
            if (!table.Equals(group.RefFullName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (parentRows[0].ContainsKey(column) && seen.Add(column))
                constraints.Add((column, val));
        }

        foreach (var colName in parentRows[0].Keys)
        {
            if (seen.Contains(colName))
                continue;
            if (!fkColumnMap.TryGetValue((group.RefFullName, colName), out var fkTarget))
                continue;
            if (!resolvedRefs.TryGetValue((fkTarget.RefTable, fkTarget.RefColumn), out var val))
                continue;
            seen.Add(colName);
            constraints.Add((colName, val));
        }

        if (constraints.Count == 0)
            return parentRows;

        return parentRows.Where(row =>
            constraints.All(c =>
                row.TryGetValue(c.ColumnName, out var v) && Equals(v, c.Value)))
            .ToList();
    }

    private sealed class FullNameColumnComparer
        : IEqualityComparer<(string Table, string Column)>
    {
        public static readonly FullNameColumnComparer Instance = new();

        public bool Equals((string Table, string Column) x, (string Table, string Column) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Table, y.Table) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Column, y.Column);

        public int GetHashCode((string Table, string Column) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Column));
    }

    private List<Dictionary<string, object>> LoadExternalFkRows(FkGroup group, int sampleSize)
    {
        if (_generatedKeys.TryGetValue(group.RefFullName, out var cached) && cached.Count > 0)
            return cached;

        var capped = Math.Clamp(sampleSize, 100, 1000);

        var refColumns = group.Columns.Select(c => c.ReferencedColumn).Distinct().ToList();
        var parsed = SqlTableName.Parse(group.RefFullName);

        var colList = string.Join(", ", refColumns.Select(c => $"[{c}]"));
        // DISTINCT and ORDER BY NEWID() cannot coexist in one SELECT in SQL Server
        // (ORDER BY expressions must appear in the select list when DISTINCT is used).
        var sql = $"""
            SELECT TOP(@SampleSize) {colList}
            FROM (
                SELECT DISTINCT {colList}
                FROM {parsed.Bracketed}
            ) AS sampled
            ORDER BY NEWID()
            """;

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

        if (_generatedKeys.TryAdd(group.RefFullName, rows))
            RecordGenerationOrder(group.RefFullName);
        else
            _generatedKeys[group.RefFullName] = rows;
        return rows;
    }

    private void RecordGenerationOrder(string fullName)
    {
        lock (_generationOrderLock)
        {
            _generationOrder.Add(fullName);
        }
    }

    #endregion

    #region Temp table SQL builders

    internal static string BuildCreateTempTableSql(
        string tempTableName,
        List<ColumnInfo> dataColumns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {tempTableName} (");
        sb.Append("    [Id] INT NOT NULL PRIMARY KEY");

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
        return $"SELECT {cols} FROM {table.BracketedName}";
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
              FROM {table.BracketedName} t
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
            Database = tablePlan.Database,
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
                    var refParsed = SqlTableName.Parse(refTable);
                    return new ForeignKeyInfo
                    {
                        FkName = Helpers.GetArgString(c.GeneratorArgs, "compositeFkGroup"),
                        Database = tablePlan.Database,
                        ParentSchema = tablePlan.Schema,
                        ParentTable = tablePlan.TableName,
                        ParentColumn = c.Name,
                        ReferencedSchema = refParsed.Schema,
                        ReferencedTable = refParsed.TableName,
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
