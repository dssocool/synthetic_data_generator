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
        var tables = await schemaReader.ReadSchemaAsync(
            string.IsNullOrWhiteSpace(scope.SchemaFilter) ? null : scope.SchemaFilter);

        var errors = new List<string>();

        if (scope.TablesToInclude.Length > 0)
        {
            var scopeErrors = CollectScopeErrors(tables, scope.TablesToInclude);
            if (scopeErrors.Count > 0)
                return ValidateScopeResult.Failure(scopeErrors);

            var includeSet = scope.GetIncludeTableNames();
            tables = tables
                .Where(t => includeSet.Contains(t.TableName) || includeSet.Contains(t.FullName))
                .ToList();
        }

        if (tables.Count == 0)
        {
            errors.Add("No tables found matching the specified scope.");
            return ValidateScopeResult.Failure(errors);
        }

        var graph = new DependencyGraph();
        graph.Build(tables, columnScope);

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
            var allTables = await ReadAllTablesAsync(command.ConnectionString, scope.SchemaFilter);
            var updateErrors = CollectUpdateScopeErrors(columnScope!, sortedTables, allTables);
            if (updateErrors.Count > 0)
                return ValidateScopeResult.Failure(updateErrors);
        }

        return new ValidateScopeResult(true, [], sortedTables, graph, columnScope);
    }

    public async Task<GeneratePlanResult> GeneratePlanAsync(
        GeneratePlanCommand command,
        CancellationToken ct)
    {
        var validation = command.ValidationResult;
        var scope = command.Scope;
        var mode = validation.ScopedTables.Count > 0 && validation.Graph != null
            ? (validation.ColumnScope != null ? "update" : "bootstrap")
            : "bootstrap";

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            validation.ScopedTables,
            validation.Graph!.SelfReferencingTables,
            scope.RowsPerTable,
            scope.Seed,
            scope.Locale,
            mode,
            validation.ColumnScope);

        if (command.OutputPath != null)
            await planGen.WritePlanAsync(plan, command.OutputPath);

        return new GeneratePlanResult(plan, command.OutputPath);
    }

    private static async Task<List<TableInfo>> ReadAllTablesAsync(
        string connectionString, string? schemaFilter)
    {
        var schemaReader = new SchemaReader(connectionString);
        return await schemaReader.ReadSchemaAsync(
            string.IsNullOrWhiteSpace(schemaFilter) ? null : schemaFilter);
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

    #region Legacy static methods kept for backward compatibility with tests

    internal static void ValidateScope(
        List<TableInfo> allTables,
        TableScope[] tablesToInclude)
    {
        var errors = CollectScopeErrors(allTables, tablesToInclude);
        if (errors.Count == 0)
            return;

        foreach (var error in errors)
            PrintFatal(error);

        var parts = new List<string>();
        var missingTables = errors.Where(e => e.Contains("does not exist in the database")).ToList();
        var missingColumns = errors.Where(e => e.Contains("does not exist in table")).ToList();

        if (missingTables.Count > 0)
            parts.Add($"{missingTables.Count} table(s) not found: {string.Join(", ", missingTables.Select(ExtractBracketedName))}");
        if (missingColumns.Count > 0)
            parts.Add($"{missingColumns.Count} column(s) not found: {string.Join(", ", missingColumns.Select(ExtractColumnRef))}");

        throw new InvalidOperationException(
            $"Scope validation failed — {string.Join("; ", parts)}.");
    }

    internal static void ValidateUpdateScope(
        Dictionary<string, HashSet<string>> columnScope,
        List<TableInfo> scopeTables,
        List<TableInfo> allTables)
    {
        foreach (var table in scopeTables)
        {
            if (table.PrimaryKeyColumns.Count == 0)
            {
                PrintFatal($"Table [{table.FullName}] has no primary key. Update mode requires a primary key.");
                throw new InvalidOperationException(
                    $"Table [{table.FullName}] has no primary key.");
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
                {
                    PrintFatal($"Column [{colName}] not found in table [{table.FullName}].");
                    throw new InvalidOperationException(
                        $"Column [{colName}] not found in table [{table.FullName}].");
                }

                if (pkSet.Contains(colName))
                {
                    PrintFatal(
                        $"Column [{table.FullName}].[{colName}] is a primary key column and cannot be updated.");
                    throw new InvalidOperationException(
                        $"Column [{table.FullName}].[{colName}] is a primary key column and cannot be updated.");
                }
            }
        }

        ValidateUpdateForeignKeys(columnScope, scopeTables, allTables);
    }

    internal static void ValidateUpdateForeignKeys(
        Dictionary<string, HashSet<string>> columnScope,
        List<TableInfo> scopeTables,
        List<TableInfo> allTables)
    {
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
                    PrintFatal(
                        $"Column [{table.FullName}].[{fk.ParentColumn}] has a foreign key reference " +
                        $"to [{fk.FullReferencedTableName}].[{fk.ReferencedColumn}], " +
                        $"but [{fk.FullReferencedTableName}].[{fk.ReferencedColumn}] is not in the update columns list. " +
                        $"Both sides of a FK relationship must be included.");
                    throw new InvalidOperationException(
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
                    PrintFatal(
                        $"Column [{fk.FullReferencedTableName}].[{fk.ReferencedColumn}] is referenced by " +
                        $"foreign key from [{fk.FullParentTableName}].[{fk.ParentColumn}], " +
                        $"but [{fk.FullParentTableName}].[{fk.ParentColumn}] is not in the update columns list. " +
                        $"Both sides of a FK relationship must be included.");
                    throw new InvalidOperationException(
                        $"FK validation failed: [{fk.FullReferencedTableName}].[{fk.ReferencedColumn}] is referenced by " +
                        $"[{fk.FullParentTableName}].[{fk.ParentColumn}] which is not in the update list.");
                }
            }
        }
    }

    private static void PrintFatal(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  FATAL: {message}");
        Console.ResetColor();
    }

    private static string ExtractBracketedName(string errorMsg)
    {
        var start = errorMsg.IndexOf('[');
        var end = errorMsg.IndexOf(']');
        return start >= 0 && end > start ? errorMsg[(start + 1)..end] : errorMsg;
    }

    private static string ExtractColumnRef(string errorMsg)
    {
        var firstOpen = errorMsg.IndexOf('[');
        var firstClose = errorMsg.IndexOf(']');
        var lastOpen = errorMsg.LastIndexOf('[');
        var lastClose = errorMsg.LastIndexOf(']');
        if (firstOpen >= 0 && firstClose > firstOpen && lastOpen > firstClose && lastClose > lastOpen)
            return $"[{errorMsg[(lastOpen + 1)..lastClose]}].[{errorMsg[(firstOpen + 1)..firstClose]}]";
        return errorMsg;
    }

    #endregion
}
