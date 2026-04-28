using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class DataGenerationPlanner : IDataGenerationPlanner
{
    public async Task<ValidateScopeResult> ValidateScopeAsync(
        ValidateScopeCommand command,
        CancellationToken ct)
    {
        var scope = command.Scope;
        var mode = command.Mode;
        var isUpdate = mode.Equals("update", StringComparison.OrdinalIgnoreCase);
        var columnScope = scope.BuildColumnScope();

        var schemaReader = new SchemaReader(command.ConnectionString);
        var allTables = await schemaReader.ReadSchemaAsync(scope.SchemaFilter);

        var tables = allTables;
        var errors = new List<string>();

        if (scope.TablesToInclude.Length > 0)
        {
            var scopeErrors = CollectScopeErrors(allTables, scope.TablesToInclude);
            if (scopeErrors.Count > 0)
                return ValidateScopeResult.Failure(scopeErrors);

            var includeSet = scope.GetIncludeTableNames();
            tables = allTables
                .Where(t => includeSet.Contains(t.TableName) || includeSet.Contains(t.FullName))
                .ToList();
        }

        if (tables.Count == 0)
        {
            errors.Add("No tables found matching the specified scope.");
            return ValidateScopeResult.Failure(errors);
        }

        var customDepGroups = ScopeConfig.ParseCustomDependencies(scope.CustomDependencies);

        var customDepErrors = CollectCustomDependencyErrors(
            customDepGroups, tables, allTables, columnScope);
        if (customDepErrors.Count > 0)
            return ValidateScopeResult.Failure(customDepErrors);

        var emptyRootErrors = await CheckExternalRootDataAsync(
            customDepGroups, command.ConnectionString, ct);
        if (emptyRootErrors.Count > 0)
            return ValidateScopeResult.Failure(emptyRootErrors);

        var graph = new DependencyGraph();
        graph.Build(tables, columnScope);
        graph.AddCustomDependencies(customDepGroups);

        List<TableInfo> sortedTables;
        try
        {
            sortedTables = graph.GetTopologicalOrder();
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
            return ValidateScopeResult.Failure(errors);
        }

        if (isUpdate)
        {
            var updateErrors = CollectUpdateScopeErrors(columnScope!, sortedTables, allTables);
            if (updateErrors.Count > 0)
                return ValidateScopeResult.Failure(updateErrors);
        }

        var externalDeps = CollectExternalDependencies(sortedTables, allTables);

        return new ValidateScopeResult(true, [], sortedTables, graph.SelfReferencingTables, columnScope,
            externalDeps.Count > 0 ? externalDeps : null,
            customDepGroups.Count > 0 ? customDepGroups : null);
    }

    public async Task<GeneratePlanResult> GeneratePlanAsync(
        GeneratePlanCommand command,
        CancellationToken ct)
    {
        var validation = command.ValidationResult;
        var scope = command.Scope;
        var mode = command.Mode;

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            validation.ScopedTables,
            validation.SelfReferencingTables ?? new HashSet<string>(),
            scope.RowsPerTable,
            scope.Seed,
            scope.Locale,
            mode,
            validation.ColumnScope,
            validation.ExternalDependencies,
            validation.CustomDependencies);

        if (command.OutputPath != null)
            await planGen.WritePlanAsync(plan, command.OutputPath);

        return new GeneratePlanResult(plan, command.OutputPath);
    }

    internal static List<string> CollectScopeErrors(
        List<TableInfo> allTables,
        TableScope[] tablesToInclude)
    {
        var tableByName = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
        var tableByFullName = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTables)
        {
            tableByName.TryAdd(t.TableName, t);
            tableByFullName.TryAdd(t.FullName, t);
        }

        var missingTables = new List<string>();
        var missingColumns = new List<(string Table, string Column)>();

        foreach (var entry in tablesToInclude)
        {
            if (!tableByFullName.TryGetValue(entry.Table, out var matched)
                && !tableByName.TryGetValue(entry.Table, out matched))
            {
                missingTables.Add(entry.Table);
                continue;
            }

            if (entry.Columns is not { Count: > 0 })
                continue;

            var dbColumns = new HashSet<string>(
                matched.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var col in entry.Columns)
            {
                if (!dbColumns.Contains(col))
                    missingColumns.Add((matched.FullName, col));
            }
        }

        var errors = new List<string>();
        foreach (var table in missingTables)
            errors.Add($"Table [{table}] does not exist in the database.");
        foreach (var (table, column) in missingColumns)
            errors.Add($"Column [{column}] does not exist in table [{table}].");

        return errors;
    }

    internal static List<string> CollectUpdateScopeErrors(
        Dictionary<string, HashSet<string>> columnScope,
        List<TableInfo> scopeTables,
        List<TableInfo> allTables)
    {
        var errors = new List<string>();

        foreach (var table in scopeTables)
        {
            if (table.PrimaryKeyColumns.Count == 0)
            {
                errors.Add($"Table [{table.FullName}] has no primary key. Update mode requires a primary key.");
                continue;
            }

            if (!columnScope.TryGetValue(table.FullName, out var columnNames))
                continue;

            var tableColumnNames = new HashSet<string>(
                table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var pkSet = new HashSet<string>(
                table.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);

            foreach (var colName in columnNames)
            {
                if (!tableColumnNames.Contains(colName))
                    errors.Add($"Column [{colName}] not found in table [{table.FullName}].");
                else if (pkSet.Contains(colName))
                    errors.Add($"Column [{table.FullName}].[{colName}] is a primary key column and cannot be updated.");
            }
        }

        if (errors.Count > 0)
            return errors;

        errors.AddRange(CollectUpdateForeignKeyErrors(columnScope, scopeTables, allTables));
        return errors;
    }

    internal static List<string> CollectUpdateForeignKeyErrors(
        Dictionary<string, HashSet<string>> columnScope,
        List<TableInfo> scopeTables,
        List<TableInfo> allTables)
    {
        var errors = new List<string>();

        foreach (var table in scopeTables)
        {
            if (!columnScope.TryGetValue(table.FullName, out var userCols))
                continue;

            foreach (var fk in table.ForeignKeys)
            {
                if (!userCols.Contains(fk.ParentColumn))
                    continue;

                if (!columnScope.TryGetValue(fk.FullReferencedTableName, out var refCols)
                    || !refCols.Contains(fk.ReferencedColumn))
                {
                    errors.Add(
                        $"FK validation failed: [{table.FullName}].[{fk.ParentColumn}] references " +
                        $"[{fk.FullReferencedTableName}].[{fk.ReferencedColumn}] which is not in the update list.");
                }
            }
        }

        foreach (var table in allTables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (!columnScope.TryGetValue(fk.FullReferencedTableName, out var refUserCols))
                    continue;
                if (!refUserCols.Contains(fk.ReferencedColumn))
                    continue;

                if (!columnScope.TryGetValue(fk.FullParentTableName, out var parentUserCols)
                    || !parentUserCols.Contains(fk.ParentColumn))
                {
                    errors.Add(
                        $"FK validation failed: [{fk.FullReferencedTableName}].[{fk.ReferencedColumn}] is referenced by " +
                        $"[{fk.FullParentTableName}].[{fk.ParentColumn}] which is not in the update list.");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Validates each <see cref="CustomColumnRef"/> exists in the live database and
    /// classifies it as either in-scope (table is in TablesToInclude AND column is
    /// in that table's Columns filter) or external root (table is outside scope, or
    /// column is excluded from a scoped table's Columns filter). After
    /// classification, picks exactly one source column per group via the cascade
    /// <c>External &gt; PrimaryKey &gt; AutoGenerated &gt; Unique &gt; first declared</c>
    /// and tags it with <see cref="CustomColumnRef.IsSource"/>. Position in the
    /// YAML no longer determines the source.
    /// Multiple external columns in the same group → fatal error.
    /// </summary>
    internal static List<string> CollectCustomDependencyErrors(
        List<CustomDependencyGroup> groups,
        List<TableInfo> scopedTables,
        List<TableInfo> allTables,
        Dictionary<string, HashSet<string>>? columnScope)
    {
        var errors = new List<string>();

        var scopedTableSet = new HashSet<string>(
            scopedTables.Select(t => t.FullName), StringComparer.OrdinalIgnoreCase);

        var allTablesByFullName = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTables)
            allTablesByFullName.TryAdd(t.FullName, t);

        var columnLookup = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTables)
            foreach (var c in t.Columns)
                columnLookup.TryAdd($"{t.FullName}.{c.Name}", c);

        foreach (var group in groups)
        {
            var groupHadResolutionFailure = false;

            foreach (var colRef in group.Columns)
            {
                if (!allTablesByFullName.TryGetValue(colRef.Table, out var table))
                {
                    errors.Add(
                        $"Custom dependency references table [{colRef.Table}] which does not exist in the database.");
                    groupHadResolutionFailure = true;
                    continue;
                }

                var hasColumn = table.Columns.Any(c =>
                    c.Name.Equals(colRef.Column, StringComparison.OrdinalIgnoreCase));
                if (!hasColumn)
                {
                    errors.Add(
                        $"Custom dependency references column [{colRef.Column}] which does not exist in table [{colRef.Table}].");
                    groupHadResolutionFailure = true;
                    continue;
                }

                var isTableInScope = scopedTableSet.Contains(colRef.Table);
                var isColumnInScope = isTableInScope
                    && (columnScope is null
                        || !columnScope.TryGetValue(colRef.Table, out var scopedCols)
                        || scopedCols.Contains(colRef.Column));

                if (!isColumnInScope)
                    colRef.IsExternalRoot = true;
            }

            if (groupHadResolutionFailure || group.Columns.Count < 2)
                continue;

            var externals = group.Columns.Where(c => c.IsExternalRoot).ToList();
            if (externals.Count > 1)
            {
                var summary = string.Join(" | ",
                    group.Columns.Select(c => $"{c.Table}.{c.Column}"));
                var list = string.Join(", ",
                    externals.Select(c => $"[{c.Table}].[{c.Column}]"));
                errors.Add(
                    $"CustomDependencies group [{summary}] has multiple external columns {list}; " +
                    "at most one column may be outside TablesToInclude.");
                continue;
            }

            var sourceIndex = ResolveSourceIndex(group, columnLookup);
            group.Columns[sourceIndex].IsSource = true;
        }

        return errors;
    }

    /// <summary>
    /// Source-resolution cascade. Walks tiers top-to-bottom; at each tier, if
    /// exactly one current candidate matches, that's the source. If more than
    /// one matches, narrow to those and continue. If none match, skip the tier.
    /// Final tiebreaker (everything tied) is the first declared column.
    /// Tiers: External &gt; PrimaryKey &gt; AutoGenerated &gt; Unique.
    /// </summary>
    internal static int ResolveSourceIndex(
        CustomDependencyGroup group,
        Dictionary<string, ColumnInfo> columnLookup)
    {
        var candidates = Enumerable.Range(0, group.Columns.Count).ToList();

        int? Narrow(Func<int, bool> predicate)
        {
            var filtered = candidates.Where(predicate).ToList();
            if (filtered.Count == 1) return filtered[0];
            if (filtered.Count > 1) candidates = filtered;
            return null;
        }

        bool Has(int i, Func<ColumnInfo, bool> p) =>
            columnLookup.TryGetValue($"{group.Columns[i].Table}.{group.Columns[i].Column}", out var c)
            && p(c);

        return Narrow(i => group.Columns[i].IsExternalRoot)
            ?? Narrow(i => Has(i, c => c.IsPrimaryKey))
            ?? Narrow(i => Has(i, c => c.IsAutoGenerated))
            ?? Narrow(i => Has(i, c => c.IsUnique))
            ?? candidates[0];
    }

    /// <summary>
    /// For every unique external root column in <paramref name="groups"/>, runs
    /// <c>SELECT TOP 1 [col] FROM [schema].[table] WHERE [col] IS NOT NULL</c>.
    /// Returns one error per empty root with the list of dependent columns that
    /// would have been populated from it.
    /// </summary>
    internal static async Task<List<string>> CheckExternalRootDataAsync(
        List<CustomDependencyGroup> groups,
        string connectionString,
        CancellationToken ct)
    {
        var errors = new List<string>();

        var rootDependents = new Dictionary<(string Table, string Column), List<string>>(
            new TableColumnComparer());

        foreach (var group in groups)
        {
            if (group.Columns.Count < 2) continue;

            foreach (var source in group.Columns.Where(c => c.IsExternalRoot))
            {
                var key = (source.Table, source.Column);
                if (!rootDependents.TryGetValue(key, out var deps))
                {
                    deps = [];
                    rootDependents[key] = deps;
                }

                foreach (var dep in group.Columns)
                {
                    if (ReferenceEquals(dep, source)) continue;
                    deps.Add($"[{dep.Table}].[{dep.Column}]");
                }
            }
        }

        if (rootDependents.Count == 0)
            return errors;

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        foreach (var ((table, column), deps) in rootDependents)
        {
            var dotIdx = table.IndexOf('.');
            var schema = dotIdx >= 0 ? table[..dotIdx] : "dbo";
            var tableName = dotIdx >= 0 ? table[(dotIdx + 1)..] : table;

            var sql = $"SELECT TOP 1 [{column}] FROM [{schema}].[{tableName}] WHERE [{column}] IS NOT NULL";

            try
            {
                await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
                var result = await cmd.ExecuteScalarAsync(ct);
                if (result is null || result is DBNull)
                {
                    errors.Add(
                        $"Custom dependency root [{table}].[{column}] has no non-null values; " +
                        $"cannot populate dependents [{string.Join(", ", deps)}].");
                }
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Failed to check custom dependency root [{table}].[{column}]: {ex.Message}");
            }
        }

        return errors;
    }

    private sealed class TableColumnComparer : IEqualityComparer<(string Table, string Column)>
    {
        public bool Equals((string Table, string Column) x, (string Table, string Column) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Table, y.Table) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Column, y.Column);

        public int GetHashCode((string Table, string Column) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Column));
    }

    internal static List<ExternalDependency> CollectExternalDependencies(
        List<TableInfo> scopedTables,
        List<TableInfo> allTables)
    {
        var scopedSet = new HashSet<string>(
            scopedTables.Select(t => t.FullName), StringComparer.OrdinalIgnoreCase);
        var deps = new List<ExternalDependency>();

        foreach (var table in scopedTables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (fk.IsSelfReferencing)
                    continue;
                if (scopedSet.Contains(fk.FullReferencedTableName))
                    continue;

                deps.Add(new ExternalDependency
                {
                    FkName = fk.FkName,
                    Direction = "outbound",
                    ScopedTable = table.FullName,
                    ScopedColumn = fk.ParentColumn,
                    ExternalTable = fk.FullReferencedTableName,
                    ExternalColumn = fk.ReferencedColumn
                });
            }
        }

        foreach (var table in allTables)
        {
            if (scopedSet.Contains(table.FullName))
                continue;

            foreach (var fk in table.ForeignKeys)
            {
                if (fk.IsSelfReferencing)
                    continue;
                if (!scopedSet.Contains(fk.FullReferencedTableName))
                    continue;

                deps.Add(new ExternalDependency
                {
                    FkName = fk.FkName,
                    Direction = "inbound",
                    ScopedTable = fk.FullReferencedTableName,
                    ScopedColumn = fk.ReferencedColumn,
                    ExternalTable = table.FullName,
                    ExternalColumn = fk.ParentColumn
                });
            }
        }

        return deps;
    }

}
