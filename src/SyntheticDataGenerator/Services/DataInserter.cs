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

    private readonly Dictionary<string, List<Dictionary<string, object>>> _generatedKeys = new();
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

    public async Task<int> InsertTableFromPlanAsync(TablePlan tablePlan)
    {
        var table = TablePlanToTableInfo(tablePlan);
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

        var columnsToInsert = tablePlan.Columns
            .Where(c => !c.Generator.Equals("skip", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var firstPassColumns = isSelfRef
            ? columnsToInsert.Where(c => !selfRefColumnNames.Contains(c.Name)).ToList()
            : columnsToInsert;

        var firstPassColumnInfos = firstPassColumns
            .Select(cp => table.Columns.First(c => c.Name.Equals(cp.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var uniqueConstraints = BuildUniqueConstraintsFromPlan(tablePlan);

        var fkGroups = BuildFkGroupsFromPlan(firstPassColumns);

        List<ForeignKeyInfo>? selfRefFks = null;
        if (isSelfRef)
        {
            selfRefFks = tablePlan.Columns
                .Where(c => selfRefColumnNames.Contains(c.Name))
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
                }).ToList();
        }

        return await InsertCoreAsync(
            table, tablePlan.FullName, tablePlan.RowCount,
            firstPassColumnInfos, uniqueConstraints,
            isSelfRef, selfRefFks,
            () => BuildRowFromFkGroups(firstPassColumns, fkGroups,
                col => _valueGen.GenerateFromPlan((ColumnPlan)col) ?? DBNull.Value),
            (ex, row) => DetectFailedColumn(ex, firstPassColumns, row));
    }

    public async Task<int> InsertTableAsync(TableInfo table, int rowCount)
    {
        var isSelfRef = _selfReferencingTables.Contains(table.FullName);
        var selfRefFks = isSelfRef
            ? table.ForeignKeys.Where(fk => fk.IsSelfReferencing).ToList()
            : [];

        var nonSelfRefFks = table.ForeignKeys.Where(fk => !fk.IsSelfReferencing).ToList();

        var columnsToInsert = table.Columns
            .Where(c => !c.IsIdentity && !c.IsComputed && !c.IsRowVersion
                        && !c.IsSequenceDefault && !PlanGenerator.IsUnsupportedType(c))
            .ToList();

        var firstPassColumns = isSelfRef
            ? columnsToInsert.Where(c => !selfRefFks.Any(fk =>
                fk.ParentColumn.Equals(c.Name, StringComparison.OrdinalIgnoreCase))).ToList()
            : columnsToInsert;

        var uniqueConstraints = table.UniqueConstraints;

        var fkGroups = BuildFkGroupsFromSchema(nonSelfRefFks);

        return await InsertCoreAsync(
            table, table.FullName, rowCount,
            firstPassColumns, uniqueConstraints,
            isSelfRef, selfRefFks,
            () => BuildRowFromFkGroups(firstPassColumns, fkGroups,
                col => _valueGen.Generate((ColumnInfo)col) ?? DBNull.Value),
            (ex, row) => DetectFailedColumn(ex, firstPassColumns, row));
    }

    private async Task<int> InsertCoreAsync(
        TableInfo table,
        string fullName,
        int rowCount,
        List<ColumnInfo> insertColumns,
        List<UniqueConstraintInfo> uniqueConstraints,
        bool isSelfRef,
        List<ForeignKeyInfo>? selfRefFks,
        Func<Dictionary<string, object?>> buildRow,
        Func<Exception, Dictionary<string, object?>, ColumnFailureDetail?> detectFailedColumn)
    {
        _generatedKeys.TryAdd(fullName, []);
        _generatedPkSets.TryAdd(fullName, []);
        InitUniqueConstraintSets(fullName, uniqueConstraints);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var insertedCount = 0;

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            for (var i = 0; i < rowCount; i++)
            {
                Dictionary<string, object?> row;
                try
                {
                    var attempt = 0;
                    string? pkKey;
                    bool uniqueOk;
                    do
                    {
                        row = buildRow();
                        pkKey = BuildPkKey(table, row);
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
                    var pkValues = await InsertRowAsync(
                        connection, transaction, table, insertColumns, row);

                    if (pkValues != null)
                        _generatedKeys[fullName].Add(pkValues);
                }
                catch (DataGenerationException) { throw; }
                catch (Exception ex) when (IsCheckConstraintViolation(ex))
                {
                    throw new DataGenerationException(
                        fullName, i, null,
                        new InvalidOperationException(
                            EnhanceCheckConstraintMessage(ex, fullName, i, table), ex));
                }
                catch (Exception ex)
                {
                    var failedCol = detectFailedColumn(ex, row);
                    throw new DataGenerationException(fullName, i, failedCol, ex);
                }

                insertedCount++;
            }

            if (isSelfRef && selfRefFks != null && _generatedKeys[fullName].Count > 0)
            {
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

    private record FkGroup(
        string RefFullName,
        List<(string ParentColumn, string ReferencedColumn, bool IsNullable)> Columns);

    private static List<FkGroup> BuildFkGroupsFromSchema(List<ForeignKeyInfo> fks)
    {
        return fks
            .GroupBy(fk => fk.FkName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FkGroup(
                g.First().FullReferencedTableName,
                g.Select(fk => (fk.ParentColumn, fk.ReferencedColumn, false)).ToList()))
            .ToList();
    }

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
                return new FkGroup(
                    refTable,
                    g.Select(c => (c.Name, Helpers.GetArgString(c.GeneratorArgs, "referencedColumn"), c.IsNullable)).ToList());
            })
            .ToList();
    }

    private Dictionary<string, object?> BuildRowFromFkGroups<T>(
        List<T> columns,
        List<FkGroup> fkGroups,
        Func<IColumnMetadata, object> generateValue) where T : IColumnMetadata
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var resolvedFkValues = ResolveFkValues(fkGroups, columns, generateValue);

        foreach (var col in columns)
        {
            if (resolvedFkValues.TryGetValue(col.Name, out var fkValue))
            {
                row[col.Name] = fkValue;
                continue;
            }

            if (col is ColumnPlan cp && cp.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase))
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

    private Dictionary<string, object?> ResolveFkValues<T>(
        List<FkGroup> fkGroups,
        List<T> columns,
        Func<IColumnMetadata, object> generateValue) where T : IColumnMetadata
    {
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in fkGroups)
        {
            if (_generatedKeys.TryGetValue(group.RefFullName, out var parentRows) && parentRows.Count > 0)
            {
                var parentRow = parentRows[_random.Next(parentRows.Count)];

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

        return resolved;
    }

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

    private static string EnhanceCheckConstraintMessage(
        Exception ex, string tableName, int rowIndex, TableInfo table)
    {
        var msg = $"CHECK constraint violation inserting row {rowIndex} into {tableName}";
        if (table.CheckConstraints.Count > 0)
        {
            var constraintDetails = string.Join("; ",
                table.CheckConstraints.Select(cc => $"{cc.Name}: {cc.Definition}"));
            msg += $". Table CHECK constraints: [{constraintDetails}]";
        }
        msg += $". SQL error: {ex.Message}";
        return msg;
    }

    private static string? FormatValuePreview(object? value)
    {
        if (value is null or DBNull) return "NULL";
        if (value is byte[] bytes)
            return $"byte[{bytes.Length}]";
        var s = value.ToString() ?? "null";
        return s.Length > 80 ? s[..80] + "..." : s;
    }

    private static TableInfo TablePlanToTableInfo(TablePlan tablePlan)
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

            var key = BuildUniqueKey(uc.Columns, row);
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

    private static string? BuildUniqueKey(List<string> columns, Dictionary<string, object?> row)
    {
        var parts = new List<string>(columns.Count);
        foreach (var col in columns)
        {
            if (row.TryGetValue(col, out var val) && val is not null and not DBNull)
                parts.Add(val.ToString()!);
            else
                return null;
        }
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

    private static SqlDbType MapSqlType(string sqlType) =>
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

    private static string? BuildPkKey(TableInfo table, Dictionary<string, object?> row)
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

    private static async Task<Dictionary<string, object>?> InsertRowAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TableInfo table,
        List<ColumnInfo> columns,
        Dictionary<string, object?> row)
    {
        if (columns.Count == 0 && !table.HasIdentityPk && !table.HasSequencePk)
            return null;

        var sb = new StringBuilder();
        var hasPkColumns = table.PrimaryKeyColumns.Count > 0;

        sb.Append($"INSERT INTO [{table.Schema}].[{table.TableName}]");

        if (columns.Count > 0)
        {
            sb.Append(" (");
            sb.Append(string.Join(", ", columns.Select(c => $"[{c.Name}]")));
            sb.Append(')');
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
        else
        {
            sb.Append(" DEFAULT VALUES");
        }

        await using var cmd = new SqlCommand(sb.ToString(), connection, transaction);

        foreach (var col in columns)
        {
            var paramValue = row.TryGetValue(col.Name, out var v) ? v ?? DBNull.Value : DBNull.Value;
            var param = new SqlParameter($"@{col.Name}", MapSqlType(col.SqlType))
            {
                Value = paramValue
            };
            if (param.SqlDbType is SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney)
            {
                param.Precision = col.Precision;
                param.Scale = col.Scale;
            }
            cmd.Parameters.Add(param);
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

        var rowsToUpdate = rows
            .Skip(1)
            .Where(_ => _random.NextDouble() < 0.7)
            .ToList();

        var groupedFks = selfRefFks
            .GroupBy(fk => fk.FkName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var targetRow in rowsToUpdate)
        {
            foreach (var group in groupedFks)
            {
                var pairs = group.ToList();
                var pkColumns = table.PrimaryKeyColumns;

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

                var setClauses = pairs.Select((fk, i) => $"[{fk.ParentColumn}] = @ParentVal{i}");
                var whereClauses = pkColumns.Select((pk, i) => $"[{pk}] = @TargetPk{i}");

                var sql = $"""
                    UPDATE [{table.Schema}].[{table.TableName}]
                       SET {string.Join(", ", setClauses)}
                     WHERE {string.Join(" AND ", whereClauses)}
                    """;

                await using var cmd = new SqlCommand(sql, connection, transaction);

                for (var i = 0; i < pairs.Count; i++)
                {
                    var refCol = pairs[i].ReferencedColumn;
                    cmd.Parameters.AddWithValue($"@ParentVal{i}",
                        parentRow.TryGetValue(refCol, out var pv) ? pv : DBNull.Value);
                }

                for (var i = 0; i < pkColumns.Count; i++)
                {
                    cmd.Parameters.AddWithValue($"@TargetPk{i}", targetRow[pkColumns[i]]);
                }

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
