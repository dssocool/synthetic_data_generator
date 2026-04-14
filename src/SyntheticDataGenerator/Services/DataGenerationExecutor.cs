using System.Diagnostics;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class DataGenerationExecutor : IDataGenerationExecutor
{
    public async Task<ExecutePlanResult> ExecutePlanAsync(
        ExecutePlanCommand command,
        CancellationToken ct)
    {
        var plan = command.Plan;
        var planMode = string.IsNullOrWhiteSpace(plan.Mode) ? "bootstrap" : plan.Mode;
        var isUpdate = planMode.Equals("update", StringComparison.OrdinalIgnoreCase);

        var sortedTables = plan.Tables.OrderBy(t => t.Order).ToList();

        var selfRefTables = new HashSet<string>(
            sortedTables
                .Where(t => t.Columns.Any(c =>
                    c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase)
                    && c.GeneratorArgs.TryGetValue("isSelfReferencing", out var sr)
                    && Helpers.IsTruthy(sr)))
                .Select(t => t.FullName));

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        if (command.PlanBasePath != null)
            valueGen.SetPlanBasePath(command.PlanBasePath);

        var inserter = new DataInserter(
            command.ConnectionString, valueGen,
            isUpdate ? new HashSet<string>() : selfRefTables);

        if (isUpdate)
            return await ExecuteUpdateTablesAsync(sortedTables, inserter, valueGen, ct);

        return await ExecuteInsertTablesAsync(sortedTables, inserter, ct);
    }

    public Task<RevertExecutionResult> RevertExecutionAsync(
        RevertExecutionCommand command,
        CancellationToken ct)
    {
        return Task.FromResult(
            new RevertExecutionResult(false, "Revert execution is not yet implemented."));
    }

    private static async Task<ExecutePlanResult> ExecuteInsertTablesAsync(
        List<TablePlan> sortedTables,
        DataInserter inserter,
        CancellationToken ct)
    {
        var details = new List<TableExecutionDetail>();
        var totalRows = 0;

        foreach (var tp in sortedTables)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var inserted = await inserter.InsertTableFromPlanAsync(tp);
                totalRows += inserted;
                details.Add(new TableExecutionDetail(tp.FullName, inserted, true, null));
            }
            catch (DataGenerationException ex)
            {
                details.Add(new TableExecutionDetail(
                    ex.TableName, 0, false, ex.InnerException?.Message ?? ex.Message));
            }
            catch (Exception ex)
            {
                details.Add(new TableExecutionDetail(tp.FullName, 0, false, ex.Message));
            }
        }

        return new ExecutePlanResult(totalRows, details);
    }

    private static async Task<ExecutePlanResult> ExecuteUpdateTablesAsync(
        List<TablePlan> sortedTables,
        DataInserter inserter,
        ColumnValueGenerator valueGen,
        CancellationToken ct)
    {
        var details = new List<TableExecutionDetail>();
        var totalRows = 0;

        foreach (var tablePlan in sortedTables)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var updated = await inserter.UpdateTableFromPlanAsync(
                    tablePlan,
                    col => valueGen.GenerateFromPlan((ColumnPlan)col) ?? DBNull.Value);
                totalRows += updated;
                details.Add(new TableExecutionDetail(tablePlan.FullName, updated, true, null));
            }
            catch (DataGenerationException ex)
            {
                details.Add(new TableExecutionDetail(
                    ex.TableName, 0, false, ex.InnerException?.Message ?? ex.Message));
            }
            catch (Exception ex)
            {
                details.Add(new TableExecutionDetail(tablePlan.FullName, 0, false, ex.Message));
            }
        }

        return new ExecutePlanResult(totalRows, details);
    }

    internal static List<DataInserter.UpdateFkGroup> BuildUpdateFkGroups(
        TableInfo table,
        List<ColumnInfo> columnsToUpdate,
        Dictionary<string, HashSet<string>> columnScope)
    {
        var groups = new List<DataInserter.UpdateFkGroup>();
        var updateColNames = new HashSet<string>(
            columnsToUpdate.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var fk in table.ForeignKeys)
        {
            if (fk.IsSelfReferencing) continue;
            if (!updateColNames.Contains(fk.ParentColumn)) continue;
            if (!columnScope.ContainsKey(fk.FullReferencedTableName)) continue;

            groups.Add(new DataInserter.UpdateFkGroup(
                fk.FullReferencedTableName, fk.ParentColumn, fk.ReferencedColumn));
        }

        return groups;
    }
}
