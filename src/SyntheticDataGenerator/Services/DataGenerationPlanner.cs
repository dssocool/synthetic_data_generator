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

        var customDepErrors = CollectCustomDependencyErrors(customDepGroups, tables);
        if (customDepErrors.Count > 0)
            return ValidateScopeResult.Failure(customDepErrors);

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

    internal static List<string> CollectCustomDependencyErrors(
        List<CustomDependencyGroup> groups,
        List<TableInfo> scopedTables)
    {
        var errors = new List<string>();
        var tableByFullName = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in scopedTables)
            tableByFullName.TryAdd(t.FullName, t);

        foreach (var group in groups)
        {
            foreach (var colRef in group.Columns)
            {
                if (!tableByFullName.TryGetValue(colRef.Table, out var table))
                {
                    errors.Add($"Custom dependency references table [{colRef.Table}] which is not in scope.");
                    continue;
                }

                var hasColumn = table.Columns.Any(c =>
                    c.Name.Equals(colRef.Column, StringComparison.OrdinalIgnoreCase));
                if (!hasColumn)
                    errors.Add($"Custom dependency references column [{colRef.Column}] which does not exist in table [{colRef.Table}].");
            }
        }

        return errors;
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
