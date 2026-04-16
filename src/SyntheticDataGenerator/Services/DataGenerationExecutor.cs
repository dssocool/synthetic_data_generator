using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class DataGenerationExecutor : IDataGenerationExecutor
{
    public async Task<ExecutePlanResult> ExecutePlanAsync(
        ExecutePlanCommand command,
        CancellationToken ct,
        Action<TableExecutionDetail>? onTableComplete = null)
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
            command.ConnectionString, valueGen, selfRefTables);

        var details = new List<TableExecutionDetail>();
        var totalRows = 0;

        await using var connection = new SqlConnection(command.ConnectionString);
        await connection.OpenAsync(ct);

        foreach (var tp in sortedTables)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                int affected;
                if (isUpdate)
                {
                    var staging = await inserter.StageToTempTableAsync(tp, connection);
                    affected = await inserter.UpdateFromTempTableAsync(staging);
                }
                else
                {
                    var gen = inserter.GenerateRows(tp);
                    affected = await inserter.InsertGeneratedRowsAsync(gen, connection);
                }

                totalRows += affected;
                var detail = new TableExecutionDetail(tp.FullName, affected, true, null);
                details.Add(detail);
                onTableComplete?.Invoke(detail);
            }
            catch (DataGenerationException ex)
            {
                var detail = new TableExecutionDetail(
                    ex.TableName, 0, false, ex.InnerException?.Message ?? ex.Message);
                details.Add(detail);
                onTableComplete?.Invoke(detail);
            }
            catch (Exception ex)
            {
                var detail = new TableExecutionDetail(tp.FullName, 0, false, ex.Message);
                details.Add(detail);
                onTableComplete?.Invoke(detail);
            }
        }

        return new ExecutePlanResult(totalRows, details);
    }

    public Task<RevertExecutionResult> RevertExecutionAsync(
        RevertExecutionCommand command,
        CancellationToken ct)
    {
        return Task.FromResult(
            new RevertExecutionResult(false, "Revert execution is not yet implemented."));
    }
}
