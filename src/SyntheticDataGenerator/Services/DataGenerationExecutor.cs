using System.Collections.Concurrent;
using System.Threading.Channels;
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
        var planMode = string.IsNullOrWhiteSpace(plan.Mode) ? "insert" : plan.Mode;
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

        await using var inserter = new DataInserter(
            command.ConnectionString, valueGen, selfRefTables,
            command.ExternalSourceBufferSize ?? 10_000,
            command.PlanBasePath);

        var maxParallel = Math.Max(1, command.MaxParallelTables);

        if (maxParallel == 1 || sortedTables.Count <= 1)
        {
            return await ExecuteSequentialAsync(
                sortedTables, inserter, isUpdate, command, plan, ct, onTableComplete);
        }

        return await ExecuteParallelAsync(
            sortedTables, inserter, isUpdate, command, plan, maxParallel, ct, onTableComplete);
    }

    private static async Task<ExecutePlanResult> ExecuteSequentialAsync(
        List<TablePlan> sortedTables,
        DataInserter inserter,
        bool isUpdate,
        ExecutePlanCommand command,
        GenerationPlan plan,
        CancellationToken ct,
        Action<TableExecutionDetail>? onTableComplete)
    {
        var details = new List<TableExecutionDetail>();
        var totalRows = 0;

        await using var connection = new SqlConnection(command.ConnectionString);
        await connection.OpenAsync(ct);

        foreach (var tp in sortedTables)
        {
            ct.ThrowIfCancellationRequested();
            var detail = await RunOneTableAsync(
                tp, inserter, isUpdate, plan, command.PlanBasePath, connection, ct);

            if (detail.Success) totalRows += detail.RowsAffected;
            details.Add(detail);
            onTableComplete?.Invoke(detail);
        }

        return new ExecutePlanResult(totalRows, details);
    }

    private static async Task<ExecutePlanResult> ExecuteParallelAsync(
        List<TablePlan> sortedTables,
        DataInserter inserter,
        bool isUpdate,
        ExecutePlanCommand command,
        GenerationPlan plan,
        int maxParallel,
        CancellationToken ct,
        Action<TableExecutionDetail>? onTableComplete)
    {
        var planByName = sortedTables.ToDictionary(
            t => t.FullName, StringComparer.OrdinalIgnoreCase);

        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tp in sortedTables)
        {
            inDegree[tp.FullName] = 0;
            dependents[tp.FullName] = [];
        }

        foreach (var tp in sortedTables)
        {
            foreach (var dep in GetTablePlanDeps(tp))
            {
                if (!planByName.ContainsKey(dep)) continue;
                if (string.Equals(dep, tp.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                if (dependents[dep].Contains(tp.FullName, StringComparer.OrdinalIgnoreCase)) continue;
                inDegree[tp.FullName]++;
                dependents[dep].Add(tp.FullName);
            }
        }

        var readyQueue = new Queue<TablePlan>(
            sortedTables.Where(t => inDegree[t.FullName] == 0));

        var completedChannel = Channel.CreateUnbounded<TableExecutionDetail>(
            new UnboundedChannelOptions { SingleReader = true });

        using var semaphore = new SemaphoreSlim(maxParallel);
        var details = new List<TableExecutionDetail>();
        var totalRows = 0;

        while (details.Count < sortedTables.Count)
        {
            ct.ThrowIfCancellationRequested();

            while (readyQueue.TryDequeue(out var tp))
            {
                await semaphore.WaitAsync(ct);
                var localTp = tp;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var detail = await RunOneTableAsync(
                            localTp, inserter, isUpdate, plan, command.PlanBasePath,
                            sharedConnection: null, ct);
                        await completedChannel.Writer.WriteAsync(detail, ct);
                    }
                    catch (Exception ex)
                    {
                        await completedChannel.Writer.WriteAsync(
                            new TableExecutionDetail(localTp.FullName, 0, false, ex.Message),
                            ct);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct);
            }

            var detail = await completedChannel.Reader.ReadAsync(ct);
            details.Add(detail);
            onTableComplete?.Invoke(detail);
            if (detail.Success) totalRows += detail.RowsAffected;

            if (dependents.TryGetValue(detail.TableName, out var depList))
            {
                foreach (var dep in depList)
                {
                    if (--inDegree[dep] == 0)
                        readyQueue.Enqueue(planByName[dep]);
                }
            }
        }

        completedChannel.Writer.Complete();
        return new ExecutePlanResult(totalRows, details);
    }

    /// <summary>
    /// Generates and inserts (or updates) a single table. Builds a per-table
    /// <see cref="ColumnValueGenerator"/> seeded from <c>(plan.Seed, table.FullName)</c>
    /// so each table is fully deterministic regardless of scheduling order
    /// when running in parallel. Bogus's <c>Faker</c> is not thread-safe, so
    /// every concurrent call gets its own instance.
    /// </summary>
    private static async Task<TableExecutionDetail> RunOneTableAsync(
        TablePlan tp,
        DataInserter inserter,
        bool isUpdate,
        GenerationPlan plan,
        string? planBasePath,
        SqlConnection? sharedConnection,
        CancellationToken ct)
    {
        try
        {
            var perTableGen = new ColumnValueGenerator(
                DeriveTableSeed(plan.Seed, tp.FullName), plan.Locale);
            if (planBasePath != null)
                perTableGen.SetPlanBasePath(planBasePath);

            int affected;
            if (isUpdate)
            {
                var staging = await inserter.StageToTempTableAsync(
                    tp, sharedConnection, perTableGen);
                affected = await inserter.UpdateFromTempTableAsync(staging);
            }
            else
            {
                var gen = inserter.GenerateRows(tp, perTableGen);
                affected = await inserter.InsertGeneratedRowsAsync(gen, sharedConnection);
            }

            return new TableExecutionDetail(tp.FullName, affected, true, null);
        }
        catch (DataGenerationException ex)
        {
            return new TableExecutionDetail(
                ex.TableName, 0, false, ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return new TableExecutionDetail(tp.FullName, 0, false, ex.Message);
        }
    }

    /// <summary>
    /// Derives a stable per-table seed from the plan-level seed plus a stable
    /// hash of the table's full name. Returns null when the plan has no seed
    /// (preserves random per-run output). Uses FNV-1a so the hash is stable
    /// across runs, processes and machines (unlike <see cref="HashCode"/>).
    /// </summary>
    private static int? DeriveTableSeed(int? planSeed, string tableFullName)
    {
        if (planSeed is null) return null;
        return planSeed.Value ^ StableHash(tableFullName);
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            const uint fnvOffset = 2166136261u;
            const uint fnvPrime = 16777619u;
            var hash = fnvOffset;
            foreach (var c in s.ToLowerInvariant())
                hash = (hash ^ c) * fnvPrime;
            return (int)hash;
        }
    }

    /// <summary>
    /// Returns the full names of tables this plan depends on for FK or
    /// customDependency value sourcing. Self-referencing FKs and external
    /// references are excluded — they impose no scheduling constraint.
    /// </summary>
    private static IEnumerable<string> GetTablePlanDeps(TablePlan tp)
    {
        foreach (var col in tp.Columns)
        {
            if (col.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase))
            {
                if (col.GeneratorArgs.TryGetValue("isExternal", out var ext)
                    && Helpers.IsTruthy(ext))
                    continue;
                if (col.GeneratorArgs.TryGetValue("isSelfReferencing", out var sr)
                    && Helpers.IsTruthy(sr))
                    continue;

                var refTable = Helpers.GetArgString(col.GeneratorArgs, "referencedTable");
                if (!string.IsNullOrEmpty(refTable))
                    yield return refTable;
            }
            else if (col.Generator.Equals("customDependency", StringComparison.OrdinalIgnoreCase))
            {
                if (col.GeneratorArgs.TryGetValue("isExternal", out var ext)
                    && Helpers.IsTruthy(ext))
                    continue;

                var srcTable = Helpers.GetArgString(col.GeneratorArgs, "sourceTable");
                if (!string.IsNullOrEmpty(srcTable))
                    yield return srcTable;
            }
        }
    }

    public Task<RevertExecutionResult> RevertExecutionAsync(
        RevertExecutionCommand command,
        CancellationToken ct)
    {
        return Task.FromResult(
            new RevertExecutionResult(false, "Revert execution is not yet implemented."));
    }
}
