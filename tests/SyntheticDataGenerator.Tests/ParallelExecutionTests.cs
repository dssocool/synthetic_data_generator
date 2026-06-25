using System.Diagnostics;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;

namespace SyntheticDataGenerator.Tests;

/// <summary>
/// Tests for <see cref="DataGenerationExecutor"/>'s parallel scheduling path.
/// Each test that exercises parallelism explicitly sets <c>maxParallelTables</c>
/// rather than relying on the default (which is <c>Environment.ProcessorCount</c>),
/// so behavior is the same on every machine.
/// </summary>
[Collection("Database")]
public class ParallelExecutionTests
{
    private readonly DatabaseFixture _fixture;
    private const int Seed = 42;

    public ParallelExecutionTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static string MakeName(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    /// <summary>
    /// Validates the scope, generates a plan, and executes it through the
    /// public <see cref="DataGenerationExecutor"/>. Returns both the plan
    /// (so tests can inspect generated columns / row counts) and the result.
    /// </summary>
    private async Task<(GenerationPlan Plan, ExecutePlanResult Result)> RunAsync(
        ScopeConfig scope, int maxParallel, string mode = "insert",
        Action<TableExecutionDetail>? onTableComplete = null)
    {
        var planner = new DataGenerationPlanner();
        var validate = await planner.ValidateScopeAsync(
            new ValidateScopeCommand(_fixture.ConnectionString, scope, mode),
            CancellationToken.None);
        Assert.True(validate.IsValid,
            "Scope validation failed: " + string.Join("; ", validate.Errors));

        var planResult = await planner.GeneratePlanAsync(
            new GeneratePlanCommand(validate, scope, null, mode),
            CancellationToken.None);

        var executor = new DataGenerationExecutor();
        var result = await executor.ExecutePlanAsync(
            new ExecutePlanCommand(
                planResult.Plan, _fixture.ConnectionString, null,
                scope.CustomDependencyBufferSize, maxParallel),
            CancellationToken.None,
            onTableComplete);

        return (planResult.Plan, result);
    }

    private async Task<int> CountAsync(string fullTableName) =>
        (int)(await _fixture.ExecuteScalarAsync(
            $"SELECT COUNT(*) FROM {fullTableName}"))!;

    private async Task TruncateAsync(string fullTableName) =>
        await _fixture.ExecuteSqlAsync(
            $"DELETE FROM {fullTableName}; " +
            $"DBCC CHECKIDENT('{fullTableName}', RESEED, 0) WITH NO_INFOMSGS;");

    // ──────────────────────────────────────────────
    // 1. Independent tables under high parallelism
    // ──────────────────────────────────────────────

    [Fact]
    public async Task IndependentTables_AllInsertedExactly_WithMaxParallel8()
    {
        // Five tables with no FKs and no custom-dep edges between them. With
        // MaxParallel >= 5 every one of them is dispatch-eligible from t=0.
        var names = Enumerable.Range(0, 5)
            .Select(i => MakeName($"TestParInd{i}_"))
            .ToList();

        foreach (var n in names)
        {
            await _fixture.ExecuteSqlAsync($"""
                CREATE TABLE dbo.{n} (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Label NVARCHAR(40) NOT NULL,
                    Amount INT NOT NULL
                )
                """);
        }

        var scope = new ScopeConfig(
            tablesToInclude: names.Select(n => new TableScope { Table = $"dbo.{n}" }).ToArray(),
            rowsPerTable: 50,
            seed: Seed,
            locale: "en",
            maxParallelTables: 8);

        var (_, result) = await RunAsync(scope, maxParallel: 8);

        Assert.Equal(names.Count, result.Tables.Count);
        Assert.All(result.Tables, t => Assert.True(t.Success, t.ErrorMessage));
        foreach (var n in names)
            Assert.Equal(50, await CountAsync($"dbo.{n}"));
    }

    // ──────────────────────────────────────────────
    // 2. FK ordering preserved under parallelism
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ForeignKey_NoOrphans_WithHighParallelism()
    {
        // One parent + three independent FK children. Children must wait for
        // parent (in-degree edge) but can run concurrently with each other.
        var parent = MakeName("TestParFkP_");
        var child1 = MakeName("TestParFkC1_");
        var child2 = MakeName("TestParFkC2_");
        var child3 = MakeName("TestParFkC3_");

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{parent} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Label NVARCHAR(40) NOT NULL
            )
            """);
        foreach (var c in new[] { child1, child2, child3 })
        {
            await _fixture.ExecuteSqlAsync($"""
                CREATE TABLE dbo.{c} (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    ParentId INT NOT NULL,
                    Note NVARCHAR(40) NOT NULL,
                    CONSTRAINT FK_{c}_Parent FOREIGN KEY (ParentId)
                        REFERENCES dbo.{parent}(Id)
                )
                """);
        }

        var tables = new[] { parent, child1, child2, child3 };
        var scope = new ScopeConfig(
            tablesToInclude: tables.Select(t => new TableScope { Table = $"dbo.{t}" }).ToArray(),
            rowsPerTable: 30,
            seed: Seed,
            locale: "en",
            maxParallelTables: 8);

        var (_, result) = await RunAsync(scope, maxParallel: 8);

        Assert.All(result.Tables, t => Assert.True(t.Success, t.ErrorMessage));

        Assert.Equal(30, await CountAsync($"dbo.{parent}"));
        foreach (var c in new[] { child1, child2, child3 })
        {
            Assert.Equal(30, await CountAsync($"dbo.{c}"));
            var orphans = (int)(await _fixture.ExecuteScalarAsync($"""
                SELECT COUNT(*) FROM dbo.{c} c
                WHERE NOT EXISTS (SELECT 1 FROM dbo.{parent} p WHERE p.Id = c.ParentId)
                """))!;
            Assert.Equal(0, orphans);
        }
    }

    // ──────────────────────────────────────────────
    // 3. Diamond/chain consistency end-to-end through the parallel executor
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DiamondChain_ConsistentRootId_WithHighParallelism()
    {
        // Same structure as Test72_DeepChainDiamond, but executed through
        // DataGenerationExecutor with MaxParallel > 1. Verifies that the
        // dependency-edge build + per-table seeded ColumnValueGenerator +
        // ConcurrentDictionary state still yield a consistent diamond.
        var root = MakeName("TestParDiaR_");
        var mid1 = MakeName("TestParDiaM1_");
        var mid2 = MakeName("TestParDiaM2_");
        var leaf = MakeName("TestParDiaL_");

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{root} (
                RootId INT IDENTITY(1,1) PRIMARY KEY,
                Label NVARCHAR(40) NOT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{mid1} (
                Mid1Id INT IDENTITY(1,1) PRIMARY KEY,
                RootId INT NOT NULL,
                Name NVARCHAR(40) NOT NULL,
                CONSTRAINT FK_{mid1}_Root FOREIGN KEY (RootId) REFERENCES dbo.{root}(RootId)
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{mid2} (
                Mid2Id INT IDENTITY(1,1) PRIMARY KEY,
                Mid1Id INT NOT NULL,
                Tag NVARCHAR(40) NOT NULL,
                CONSTRAINT FK_{mid2}_Mid1 FOREIGN KEY (Mid1Id) REFERENCES dbo.{mid1}(Mid1Id)
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{leaf} (
                LeafId INT IDENTITY(1,1) PRIMARY KEY,
                RootId INT NOT NULL,
                Mid2Id INT NOT NULL,
                Info NVARCHAR(40) NOT NULL,
                CONSTRAINT FK_{leaf}_Root FOREIGN KEY (RootId) REFERENCES dbo.{root}(RootId),
                CONSTRAINT FK_{leaf}_Mid2 FOREIGN KEY (Mid2Id) REFERENCES dbo.{mid2}(Mid2Id)
            )
            """);

        var tables = new[] { root, mid1, mid2, leaf };
        var scope = new ScopeConfig(
            tablesToInclude: tables.Select(t => new TableScope { Table = $"dbo.{t}" }).ToArray(),
            rowsPerTable: 20,
            seed: Seed,
            locale: "en",
            maxParallelTables: 8);

        var (_, result) = await RunAsync(scope, maxParallel: 8);
        Assert.All(result.Tables, t => Assert.True(t.Success, t.ErrorMessage));

        var mismatch = (int)(await _fixture.ExecuteScalarAsync($"""
            SELECT COUNT(*) FROM dbo.{leaf} l
            INNER JOIN dbo.{mid2} m2 ON m2.Mid2Id = l.Mid2Id
            INNER JOIN dbo.{mid1} m1 ON m1.Mid1Id = m2.Mid1Id
            WHERE m1.RootId <> l.RootId
            """))!;
        Assert.Equal(0, mismatch);
    }

    // ──────────────────────────────────────────────
    // 4. CustomDependencies-driven scheduling edge
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CustomDependency_OrderRespected_WithHighParallelism()
    {
        // Source table has no FK to its dependents — only the
        // CustomDependencies edge orders them. The executor must still
        // schedule the source to fully complete before the dependent runs;
        // otherwise the dependent's RegionCode column would generate random
        // strings instead of values copied from the source.
        var srcName = MakeName("TestParCdS_");
        var depName = MakeName("TestParCdD_");

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{srcName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Region NVARCHAR(20) NOT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{depName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                RegionCode NVARCHAR(20) NOT NULL
            )
            """);

        var validRegions = new[] { "APAC", "EMEA", "AMER", "LATAM" };
        var scope = new ScopeConfig(
            tablesToInclude:
            [
                new TableScope { Table = $"dbo.{srcName}" },
                new TableScope { Table = $"dbo.{depName}" }
            ],
            rowsPerTable: 30,
            seed: Seed,
            locale: "en",
            customDependencies: [$"dbo.{srcName}.Region|dbo.{depName}.RegionCode"],
            customValueLists:
            [
                new CustomValueList
                {
                    Column = $"dbo.{srcName}.Region",
                    Values = validRegions.ToList()
                }
            ],
            maxParallelTables: 8);

        var (_, result) = await RunAsync(scope, maxParallel: 8);
        Assert.All(result.Tables, t => Assert.True(t.Success, t.ErrorMessage));

        var validSet = new HashSet<string>(validRegions);
        var depRows = await _fixture.ExecuteQueryAsync(
            $"SELECT RegionCode FROM dbo.{depName}");
        Assert.Equal(30, depRows.Count);
        foreach (var row in depRows)
            Assert.Contains((string)row["RegionCode"]!, validSet);
    }

    // ──────────────────────────────────────────────
    // 5. Per-table seed determinism: same seed twice in parallel mode
    // ──────────────────────────────────────────────

    [Fact]
    public async Task SameSeed_RunTwice_InParallel_ProducesIdenticalPerTableData()
    {
        // The per-table ColumnValueGenerator is seeded from
        // (plan.Seed, table.FullName), so two runs against the SAME tables
        // with the SAME seed must produce the same set of values in each
        // table — even though the scheduler may interleave them differently.
        var t1 = MakeName("TestParSeedA_");
        var t2 = MakeName("TestParSeedB_");
        var t3 = MakeName("TestParSeedC_");

        foreach (var n in new[] { t1, t2, t3 })
        {
            await _fixture.ExecuteSqlAsync($"""
                CREATE TABLE dbo.{n} (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Code NVARCHAR(40) NOT NULL,
                    Score INT NOT NULL
                )
                """);
        }

        var scope = new ScopeConfig(
            tablesToInclude:
            [
                new TableScope { Table = $"dbo.{t1}" },
                new TableScope { Table = $"dbo.{t2}" },
                new TableScope { Table = $"dbo.{t3}" }
            ],
            rowsPerTable: 25,
            seed: Seed,
            locale: "en",
            maxParallelTables: 8);

        await RunAsync(scope, maxParallel: 8);
        var firstRun = await SnapshotAsync(t1, t2, t3);

        foreach (var n in new[] { t1, t2, t3 })
            await TruncateAsync($"dbo.{n}");

        await RunAsync(scope, maxParallel: 8);
        var secondRun = await SnapshotAsync(t1, t2, t3);

        foreach (var n in new[] { t1, t2, t3 })
        {
            Assert.Equal(firstRun[n], secondRun[n]);
        }
    }

    // ──────────────────────────────────────────────
    // 6. Sequential vs parallel produce the same data per table when seeded
    // ──────────────────────────────────────────────

    [Fact]
    public async Task SameSeed_SequentialVsParallel_ProduceIdenticalPerTableData()
    {
        // Determinism guarantee from the plan: per-table seed derives from
        // (plan.Seed, table.FullName), so MaxParallelTables must NOT change
        // each table's content. Verifies the fast path (=1) and the parallel
        // path produce the same rows row-for-row.
        var t1 = MakeName("TestParEqA_");
        var t2 = MakeName("TestParEqB_");
        var t3 = MakeName("TestParEqC_");

        foreach (var n in new[] { t1, t2, t3 })
        {
            await _fixture.ExecuteSqlAsync($"""
                CREATE TABLE dbo.{n} (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Code NVARCHAR(40) NOT NULL,
                    Score INT NOT NULL
                )
                """);
        }

        var tablesScope = new[]
        {
            new TableScope { Table = $"dbo.{t1}" },
            new TableScope { Table = $"dbo.{t2}" },
            new TableScope { Table = $"dbo.{t3}" }
        };

        var sequentialScope = new ScopeConfig(
            tablesToInclude: tablesScope,
            rowsPerTable: 25, seed: Seed, locale: "en",
            maxParallelTables: 1);
        await RunAsync(sequentialScope, maxParallel: 1);
        var sequentialData = await SnapshotAsync(t1, t2, t3);

        foreach (var n in new[] { t1, t2, t3 })
            await TruncateAsync($"dbo.{n}");

        var parallelScope = new ScopeConfig(
            tablesToInclude: tablesScope,
            rowsPerTable: 25, seed: Seed, locale: "en",
            maxParallelTables: 8);
        await RunAsync(parallelScope, maxParallel: 8);
        var parallelData = await SnapshotAsync(t1, t2, t3);

        foreach (var n in new[] { t1, t2, t3 })
            Assert.Equal(sequentialData[n], parallelData[n]);
    }

    // ──────────────────────────────────────────────
    // 7. Failure isolation: bad table doesn't block independent siblings
    // ──────────────────────────────────────────────

    [Fact]
    public async Task FailureInOneTable_DoesNotBlockIndependentSiblings_InParallel()
    {
        // Doomed table has a CHECK constraint that no generator can satisfy,
        // so its insert MUST fail. The two unrelated siblings must still
        // succeed and be reported as Success: true in the result.
        var doomed = MakeName("TestParFailX_");
        var ok1 = MakeName("TestParFailA_");
        var ok2 = MakeName("TestParFailB_");

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{doomed} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Status NVARCHAR(20) NOT NULL,
                CONSTRAINT CK_{doomed}_Impossible
                    CHECK (Status = 'NEVER_GENERATED_VALUE_XYZQQQ')
            )
            """);
        foreach (var n in new[] { ok1, ok2 })
        {
            await _fixture.ExecuteSqlAsync($"""
                CREATE TABLE dbo.{n} (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Label NVARCHAR(40) NOT NULL
                )
                """);
        }

        var scope = new ScopeConfig(
            tablesToInclude:
            [
                new TableScope { Table = $"dbo.{doomed}" },
                new TableScope { Table = $"dbo.{ok1}" },
                new TableScope { Table = $"dbo.{ok2}" }
            ],
            rowsPerTable: 10,
            seed: Seed,
            locale: "en",
            maxParallelTables: 8);

        var (_, result) = await RunAsync(scope, maxParallel: 8);

        var doomedDetail = result.Tables.Single(t =>
            t.TableName.Equals($"dbo.{doomed}", StringComparison.OrdinalIgnoreCase));
        Assert.False(doomedDetail.Success);

        foreach (var n in new[] { ok1, ok2 })
        {
            var detail = result.Tables.Single(t =>
                t.TableName.Equals($"dbo.{n}", StringComparison.OrdinalIgnoreCase));
            Assert.True(detail.Success, detail.ErrorMessage);
            Assert.Equal(10, await CountAsync($"dbo.{n}"));
        }
    }

    // ──────────────────────────────────────────────
    // 8. Self-referencing table works in parallel mode
    // ──────────────────────────────────────────────

    [Fact]
    public async Task SelfReferencingTable_PlusUnrelatedSibling_BothSucceed_InParallel()
    {
        // The self-ref table is its own dependency in the executor's edge
        // model (we exclude self-references from scheduling), so it must
        // still be dispatchable AND complete correctly when running alongside
        // an unrelated sibling.
        var selfRef = MakeName("TestParSelf_");
        var sibling = MakeName("TestParSib_");

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{selfRef} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                ParentId INT NULL,
                Label NVARCHAR(40) NOT NULL,
                CONSTRAINT FK_{selfRef}_Self FOREIGN KEY (ParentId) REFERENCES dbo.{selfRef}(Id)
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{sibling} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Note NVARCHAR(40) NOT NULL
            )
            """);

        var scope = new ScopeConfig(
            tablesToInclude:
            [
                new TableScope { Table = $"dbo.{selfRef}" },
                new TableScope { Table = $"dbo.{sibling}" }
            ],
            rowsPerTable: 20,
            seed: Seed,
            locale: "en",
            maxParallelTables: 8);

        var (_, result) = await RunAsync(scope, maxParallel: 8);
        Assert.All(result.Tables, t => Assert.True(t.Success, t.ErrorMessage));

        Assert.Equal(20, await CountAsync($"dbo.{selfRef}"));
        Assert.Equal(20, await CountAsync($"dbo.{sibling}"));

        var orphans = (int)(await _fixture.ExecuteScalarAsync($"""
            SELECT COUNT(*) FROM dbo.{selfRef} c
            WHERE c.ParentId IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.{selfRef} p WHERE p.Id = c.ParentId)
            """))!;
        Assert.Equal(0, orphans);
    }

    // ──────────────────────────────────────────────
    // 9. The scheduler actually runs unrelated tables concurrently
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ParallelMode_DispatchesIndependentTablesConcurrently()
    {
        // Records every TableExecutionDetail's completion time on the
        // onTableComplete callback (which the scheduler invokes in completion
        // order). With six independent tables and MaxParallel=4, AT LEAST
        // two tables must overlap in the wall-clock sense — i.e., the gap
        // between the first completion and the last completion must be a lot
        // smaller than the sum of all per-table durations measured in the
        // strictly sequential (MaxParallel=1) baseline.
        const int N = 6;
        const int RowCount = 400;
        var names = Enumerable.Range(0, N)
            .Select(i => MakeName($"TestParConc{i}_"))
            .ToList();

        foreach (var n in names)
        {
            await _fixture.ExecuteSqlAsync($"""
                CREATE TABLE dbo.{n} (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Label NVARCHAR(40) NOT NULL,
                    Score INT NOT NULL
                )
                """);
        }

        var tablesScope = names.Select(n => new TableScope { Table = $"dbo.{n}" }).ToArray();

        var sequentialScope = new ScopeConfig(
            tablesToInclude: tablesScope,
            rowsPerTable: RowCount, seed: Seed, locale: "en",
            maxParallelTables: 1);

        var sequentialSw = Stopwatch.StartNew();
        await RunAsync(sequentialScope, maxParallel: 1);
        sequentialSw.Stop();

        foreach (var n in names)
            await TruncateAsync($"dbo.{n}");

        var parallelScope = new ScopeConfig(
            tablesToInclude: tablesScope,
            rowsPerTable: RowCount, seed: Seed, locale: "en",
            maxParallelTables: 4);

        var parallelSw = Stopwatch.StartNew();
        await RunAsync(parallelScope, maxParallel: 4);
        parallelSw.Stop();

        // Generous margin so this is not flaky on slow CI: parallel must be
        // at least 25% faster than fully sequential. Real speedup on a quad
        // with 6 independent tables is closer to 2-3x.
        Assert.True(
            parallelSw.ElapsedMilliseconds < sequentialSw.ElapsedMilliseconds * 0.75,
            $"Expected parallel run to be meaningfully faster than sequential. " +
            $"Sequential={sequentialSw.ElapsedMilliseconds}ms, " +
            $"Parallel={parallelSw.ElapsedMilliseconds}ms. " +
            $"This may indicate the scheduler is not actually running tables concurrently.");
    }

    // ──────────────────────────────────────────────
    // Snapshot helper (sorted, value-comparable)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Reads each table's rows ordered by Id and projects them into a
    /// canonical "col=val|col=val" string per row, returning a list per
    /// table. The string form bypasses Dictionary reference-equality so
    /// xUnit's <c>Assert.Equal(List, List)</c> sees true value equality.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> SnapshotAsync(params string[] tableNames)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in tableNames)
        {
            var rows = await _fixture.ExecuteQueryAsync(
                $"SELECT * FROM dbo.{name} ORDER BY Id");
            var canonical = rows
                .Select(r => string.Join("|",
                    r.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                     .Where(kv => !kv.Key.Equals("Id", StringComparison.OrdinalIgnoreCase))
                     .Select(kv => $"{kv.Key}={kv.Value ?? "<NULL>"}")))
                .ToList();
            result[name] = canonical;
        }
        return result;
    }
}
