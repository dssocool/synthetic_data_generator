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

    // schema.table -> list of PK row dictionaries (colName -> value)
    private readonly Dictionary<string, List<Dictionary<string, object>>> _generatedKeys = new();

    // schema.table -> set of serialized PK tuples for duplicate detection
    private readonly Dictionary<string, HashSet<string>> _generatedPkSets = new();

    // schema.table -> constraintName -> set of serialized unique tuples
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

        var uniqueConstraints = BuildUniqueConstraintsFromPlan(tablePlan);

        _generatedKeys.TryAdd(tablePlan.FullName, []);
        _generatedPkSets.TryAdd(tablePlan.FullName, []);
        InitUniqueConstraintSets(tablePlan.FullName, uniqueConstraints);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var insertedCount = 0;

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
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
                        row = BuildRowFromPlan(firstPassColumns, tablePlan);
                        pkKey = BuildPkKey(table, row);
                        uniqueOk = TryAddUniqueKeys(tablePlan.FullName, uniqueConstraints, row, attempt > 0);
                        attempt++;
                    } while ((!uniqueOk || (pkKey != null
                             && !_generatedPkSets[tablePlan.FullName].Add(pkKey)))
                             && attempt < MaxPkRetries);

                    if (attempt >= MaxPkRetries)
                        throw new InvalidOperationException(
                            $"Could not generate unique values for [{tablePlan.FullName}] " +
                            $"after {MaxPkRetries} attempts. Consider reducing RowsPerTable or " +
                            $"using a wider value range.");
                }
                catch (DataGenerationException) { throw; }
                catch (Exception ex)
                {
                    throw new DataGenerationException(
                        tablePlan.FullName, i, null, ex);
                }

                try
                {
                    var pkValues = await InsertRowAsync(
                        connection, transaction, table, firstPassColumnInfos, row);

                    if (pkValues != null)
                        _generatedKeys[tablePlan.FullName].Add(pkValues);
                }
                catch (DataGenerationException) { throw; }
                catch (Exception ex) when (IsCheckConstraintViolation(ex))
                {
                    throw new DataGenerationException(
                        tablePlan.FullName, i, null,
                        new InvalidOperationException(
                            EnhanceCheckConstraintMessage(ex, tablePlan.FullName, i, table), ex));
                }
                catch (Exception ex)
                {
                    var failedCol = DetectFailedColumnFromPlan(ex, firstPassColumns, row);
                    throw new DataGenerationException(
                        tablePlan.FullName, i, failedCol, ex);
                }

                insertedCount++;
            }

            if (isSelfRef && _generatedKeys[tablePlan.FullName].Count > 0)
            {
                var selfRefFks = selfRefColumns.Select(c =>
                {
                    var args = c.GeneratorArgs;
                    return new ForeignKeyInfo
                    {
                        FkName = GetArgString(args, "compositeFkGroup"),
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
            .Where(c => !c.IsIdentity && !c.IsComputed && !c.IsRowVersion
                        && !PlanGenerator.IsUnsupportedType(c))
            .ToList();

        var firstPassColumns = isSelfRef
            ? columnsToInsert.Where(c => !selfRefFks.Any(fk =>
                fk.ParentColumn.Equals(c.Name, StringComparison.OrdinalIgnoreCase))).ToList()
            : columnsToInsert;

        var uniqueConstraints = table.UniqueConstraints;

        _generatedKeys.TryAdd(table.FullName, []);
        _generatedPkSets.TryAdd(table.FullName, []);
        InitUniqueConstraintSets(table.FullName, uniqueConstraints);

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
                        row = BuildRow(firstPassColumns, nonSelfRefFks, fkColumnNames, table);
                        pkKey = BuildPkKey(table, row);
                        uniqueOk = TryAddUniqueKeys(table.FullName, uniqueConstraints, row, attempt > 0);
                        attempt++;
                    } while ((!uniqueOk || (pkKey != null
                             && !_generatedPkSets[table.FullName].Add(pkKey)))
                             && attempt < MaxPkRetries);

                    if (attempt >= MaxPkRetries)
                        throw new InvalidOperationException(
                            $"Could not generate unique values for [{table.FullName}] " +
                            $"after {MaxPkRetries} attempts. Consider reducing RowsPerTable or " +
                            $"using a wider value range.");
                }
                catch (DataGenerationException) { throw; }
                catch (Exception ex)
                {
                    throw new DataGenerationException(
                        table.FullName, i, null, ex);
                }

                try
                {
                    var pkValues = await InsertRowAsync(
                        connection, transaction, table, firstPassColumns, row);

                    if (pkValues != null)
                        _generatedKeys[table.FullName].Add(pkValues);
                }
                catch (DataGenerationException) { throw; }
                catch (Exception ex) when (IsCheckConstraintViolation(ex))
                {
                    throw new DataGenerationException(
                        table.FullName, i, null,
                        new InvalidOperationException(
                            EnhanceCheckConstraintMessage(ex, table.FullName, i, table), ex));
                }
                catch (Exception ex)
                {
                    var failedCol = DetectFailedColumn(ex, firstPassColumns, row);
                    throw new DataGenerationException(
                        table.FullName, i, failedCol, ex);
                }

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

        var resolvedFkValues = ResolveGroupedFkValuesFromPlan(columns);

        foreach (var colPlan in columns)
        {
            if (colPlan.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase))
            {
                if (resolvedFkValues.TryGetValue(colPlan.Name, out var fkValue))
                {
                    row[colPlan.Name] = fkValue;
                    continue;
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

    private Dictionary<string, object?> ResolveGroupedFkValuesFromPlan(List<ColumnPlan> columns)
    {
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var fkColumns = columns
            .Where(c => c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var grouped = fkColumns
            .GroupBy(c => GetArgString(c.GeneratorArgs, "compositeFkGroup"), StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var members = group.ToList();
            var first = members[0];
            var refSchema = GetArgString(first.GeneratorArgs, "referencedSchema");
            var refTable = GetArgString(first.GeneratorArgs, "referencedTable");
            var refFullName = $"{refSchema}.{refTable}";

            if (_generatedKeys.TryGetValue(refFullName, out var parentRows) && parentRows.Count > 0)
            {
                var parentRow = parentRows[_random.Next(parentRows.Count)];

                foreach (var col in members)
                {
                    var refColumn = GetArgString(col.GeneratorArgs, "referencedColumn");
                    if (parentRow.TryGetValue(refColumn, out var value))
                        resolved[col.Name] = value;
                }
            }
            else
            {
                foreach (var col in members)
                {
                    resolved[col.Name] = col.IsNullable
                        ? DBNull.Value
                        : (_valueGen.GenerateFromPlan(col) ?? DBNull.Value);
                }
            }
        }

        return resolved;
    }

    private Dictionary<string, object?> BuildRow(
        List<ColumnInfo> columns,
        List<ForeignKeyInfo> nonSelfRefFks,
        HashSet<string> fkColumnNames,
        TableInfo table)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var resolvedFkValues = ResolveGroupedFkValues(table, nonSelfRefFks, columns);

        foreach (var col in columns)
        {
            if (resolvedFkValues.TryGetValue(col.Name, out var fkValue))
            {
                row[col.Name] = fkValue;
                continue;
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

    private Dictionary<string, object?> ResolveGroupedFkValues(
        TableInfo table,
        List<ForeignKeyInfo> fks,
        List<ColumnInfo> columns)
    {
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var grouped = fks
            .GroupBy(fk => fk.FkName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var pairs = group.ToList();
            var refFullName = pairs[0].FullReferencedTableName;

            if (_generatedKeys.TryGetValue(refFullName, out var parentRows) && parentRows.Count > 0)
            {
                var parentRow = parentRows[_random.Next(parentRows.Count)];

                foreach (var fk in pairs)
                {
                    if (parentRow.TryGetValue(fk.ReferencedColumn, out var value))
                        resolved[fk.ParentColumn] = value;
                }
            }
            else
            {
                foreach (var fk in pairs)
                {
                    var col = columns.FirstOrDefault(c =>
                        c.Name.Equals(fk.ParentColumn, StringComparison.OrdinalIgnoreCase));
                    resolved[fk.ParentColumn] = col is { IsNullable: true }
                        ? DBNull.Value
                        : (col != null ? _valueGen.Generate(col) ?? DBNull.Value : DBNull.Value);
                }
            }
        }

        return resolved;
    }

    private static ColumnFailureDetail? DetectFailedColumn(
        Exception ex,
        List<ColumnInfo> columns,
        Dictionary<string, object?> row)
    {
        var msg = ex.Message;
        foreach (var c in columns)
        {
            if (!msg.Contains(c.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            row.TryGetValue(c.Name, out var val);
            return new ColumnFailureDetail
            {
                ColumnName = c.Name,
                SqlType = c.SqlType,
                MaxLength = c.MaxLength,
                Precision = c.Precision,
                Scale = c.Scale,
                Generator = "(auto)",
                GeneratedValueType = val is null or DBNull ? null : val.GetType().Name,
                GeneratedValuePreview = FormatValuePreview(val),
            };
        }
        return null;
    }

    private static ColumnFailureDetail? DetectFailedColumnFromPlan(
        Exception ex,
        List<ColumnPlan> columns,
        Dictionary<string, object?> row)
    {
        var msg = ex.Message;
        foreach (var c in columns)
        {
            if (!msg.Contains(c.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            row.TryGetValue(c.Name, out var val);
            return new ColumnFailureDetail
            {
                ColumnName = c.Name,
                SqlType = c.SqlType,
                MaxLength = c.MaxLength,
                Precision = c.Precision,
                Scale = c.Scale,
                Generator = c.Generator,
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
                IsRowVersion = cp.IsRowVersion,
                IsUnique = cp.IsUnique,
                DefaultDefinition = cp.HasDefault ? "(from plan)" : null,
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
                    FkName = GetArgString(c.GeneratorArgs, "compositeFkGroup"),
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

        // Unrecognized predicate: fall back to full-table uniqueness (stricter but safe)
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

    private static bool IsTruthy(object? value)
    {
        if (value is bool b) return b;
        if (value is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static string GetArgString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return string.Empty;
        if (value is string s) return s;
        return value.ToString() ?? string.Empty;
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
        if (columns.Count == 0 && !table.HasIdentityPk)
            return null;

        var sb = new StringBuilder();
        var hasPkColumns = table.PrimaryKeyColumns.Count > 0;
        var identityPkCols = table.Columns
            .Where(c => c.IsPrimaryKey && c.IsIdentity)
            .Select(c => c.Name)
            .ToList();

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
