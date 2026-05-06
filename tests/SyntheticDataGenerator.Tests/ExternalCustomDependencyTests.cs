using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;

namespace SyntheticDataGenerator.Tests;

[Collection("Database")]
public class ExternalCustomDependencyTests
{
    private readonly DatabaseFixture _fixture;

    public ExternalCustomDependencyTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    // ──────────────────────────────────────────────
    // ExternalSourceStreamer (live DB, small table)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ExternalSourceStreamer_PicksRandomFromBuffer()
    {
        var tableName = "TestExtStreamer_" + Guid.NewGuid().ToString("N")[..8];
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{tableName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code INT NOT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            INSERT INTO dbo.{tableName} (Code) VALUES (10), (20), (30), (40), (50)
            """);

        await using var streamer = new ExternalSourceStreamer(
            _fixture.ConnectionString, $"dbo.{tableName}", "Code", bufferSize: 16,
            random: new Random(42));

        var picked = new HashSet<int>();
        for (var i = 0; i < 200; i++)
            picked.Add((int)streamer.Pick());

        // All 5 values must be reachable through the rotating buffer.
        Assert.Contains(10, picked);
        Assert.Contains(20, picked);
        Assert.Contains(30, picked);
        Assert.Contains(40, picked);
        Assert.Contains(50, picked);
    }

    [Fact]
    public async Task ExternalSourceStreamer_HandlesSmallTableSmallerThanBuffer()
    {
        // Buffer size 100 but only 3 rows in the table — streamer must serve
        // those 3 values indefinitely without errors.
        var tableName = "TestExtStreamerSmall_" + Guid.NewGuid().ToString("N")[..8];
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{tableName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(10) NOT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            INSERT INTO dbo.{tableName} (Code) VALUES ('a'), ('b'), ('c')
            """);

        await using var streamer = new ExternalSourceStreamer(
            _fixture.ConnectionString, $"dbo.{tableName}", "Code", bufferSize: 100,
            random: new Random(42));

        var seen = new HashSet<string>();
        for (var i = 0; i < 50; i++)
            seen.Add((string)streamer.Pick());

        Assert.Equal(new[] { "a", "b", "c" }.OrderBy(x => x),
                     seen.OrderBy(x => x));
    }

    [Fact]
    public async Task ExternalSourceStreamer_BufferSizeOne_StillWorks()
    {
        // Extreme: single-slot buffer. Window slides one value at a time;
        // every value in the source must still become reachable.
        var tableName = "TestExtStreamerBuf1_" + Guid.NewGuid().ToString("N")[..8];
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{tableName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code INT NOT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            INSERT INTO dbo.{tableName} (Code) VALUES (1), (2), (3), (4), (5)
            """);

        await using var streamer = new ExternalSourceStreamer(
            _fixture.ConnectionString, $"dbo.{tableName}", "Code", bufferSize: 1,
            random: new Random(42));

        var seen = new HashSet<int>();
        for (var i = 0; i < 50; i++)
            seen.Add((int)streamer.Pick());

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task ExternalSourceStreamer_LargeTableRotatesBeyondInitialBuffer()
    {
        // Source has 200 rows, buffer holds only 10. Without rotation we'd
        // only ever see values 1..10. With rotation, picks late in the run
        // should reveal values from the back half of the table.
        var tableName = "TestExtStreamerLarge_" + Guid.NewGuid().ToString("N")[..8];
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{tableName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code INT NOT NULL
            )
            """);
        // Bulk-insert 200 rows.
        await _fixture.ExecuteSqlAsync($"""
            ;WITH N AS (
                SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO dbo.{tableName} (Code) SELECT n FROM N
            """);

        await using var streamer = new ExternalSourceStreamer(
            _fixture.ConnectionString, $"dbo.{tableName}", "Code", bufferSize: 10,
            random: new Random(42));

        var seen = new HashSet<int>();
        for (var i = 0; i < 1000; i++)
            seen.Add((int)streamer.Pick());

        Assert.True(seen.Count > 50,
            $"Expected the rotating window to expose many distinct values; got {seen.Count}");
        Assert.True(seen.Any(v => v > 100),
            "Expected the rotating window to slide past the initial buffer of 10");
        Assert.True(seen.Any(v => v >= 150),
            "Expected the window to reach values near the end of the source");
    }

    [Fact]
    public async Task ExternalSourceStreamer_ThrowsOnAllNullColumn()
    {
        // The streamer's WHERE [col] IS NOT NULL filter strips every row, so
        // the buffer ends up empty. Pick() must fail loudly rather than
        // silently returning a default.
        var tableName = "TestExtStreamerNulls_" + Guid.NewGuid().ToString("N")[..8];
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{tableName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code INT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            INSERT INTO dbo.{tableName} (Code) VALUES (NULL), (NULL), (NULL)
            """);

        await using var streamer = new ExternalSourceStreamer(
            _fixture.ConnectionString, $"dbo.{tableName}", "Code", bufferSize: 10,
            random: new Random(42));

        var ex = Assert.Throws<InvalidOperationException>(() => streamer.Pick());
        Assert.Contains(tableName, ex.Message);
        Assert.Contains("no non-null values", ex.Message);
    }

    [Fact]
    public async Task ExternalSourceStreamer_FiltersOutNullsFromMixedColumn()
    {
        // The WHERE filter must keep the streamer from ever returning NULL
        // even when the source column is sparsely populated.
        var tableName = "TestExtStreamerMixed_" + Guid.NewGuid().ToString("N")[..8];
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{tableName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code INT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            INSERT INTO dbo.{tableName} (Code)
            VALUES (NULL), (10), (NULL), (20), (NULL), (30), (NULL)
            """);

        await using var streamer = new ExternalSourceStreamer(
            _fixture.ConnectionString, $"dbo.{tableName}", "Code", bufferSize: 16,
            random: new Random(42));

        var seen = new HashSet<int>();
        for (var i = 0; i < 100; i++)
        {
            var pick = streamer.Pick();
            Assert.NotNull(pick);
            Assert.IsNotType<DBNull>(pick);
            seen.Add((int)pick);
        }

        Assert.Equal(new[] { 10, 20, 30 }.OrderBy(x => x), seen.OrderBy(x => x));
    }

    // ──────────────────────────────────────────────
    // End-to-end: validation + execution with an external root
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ExternalRoot_PopulatesDependentFromLiveSource()
    {
        var lookupName = "TestExtLookup_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName = "TestExtOrders_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{lookupName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            INSERT INTO dbo.{lookupName} (Code)
            VALUES ('ALPHA'), ('BETA'), ('GAMMA'), ('DELTA')
            """);

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                LookupCode NVARCHAR(20) NOT NULL,
                Amount INT NOT NULL
            )
            """);

        var scope = new ScopeConfig(
            schemaFilter: ["dbo"],
            tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
            rowsPerTable: 25,
            seed: 42,
            locale: "en",
            customDependencies: [$"dbo.{lookupName}.Code|dbo.{ordersName}.LookupCode"],
            customDependencyBufferSize: 16);

        var planner = new DataGenerationPlanner();
        var executor = new DataGenerationExecutor();
        var orchestrator = new GeneratorOrchestrator(
            _fixture.ConnectionString, scope, planner, executor);

        await orchestrator.RunDirectAsync("insert");

        var rows = await _fixture.ExecuteQueryAsync(
            $"SELECT LookupCode FROM dbo.{ordersName}");

        Assert.Equal(25, rows.Count);
        var validCodes = new HashSet<string> { "ALPHA", "BETA", "GAMMA", "DELTA" };
        foreach (var row in rows)
        {
            var code = (string)row["LookupCode"]!;
            Assert.Contains(code, validCodes);
        }
    }

    [Fact]
    public async Task ExternalRoot_EmptyTableFailsFastDuringValidation()
    {
        var lookupName = "TestExtLookupEmpty_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName = "TestExtOrdersFail_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{lookupName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                LookupCode NVARCHAR(20) NOT NULL
            )
            """);

        var scope = new ScopeConfig(
            schemaFilter: ["dbo"],
            tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
            rowsPerTable: 5,
            seed: 42,
            locale: "en",
            customDependencies: [$"dbo.{lookupName}.Code|dbo.{ordersName}.LookupCode"]);

        var planner = new DataGenerationPlanner();
        var validateResult = await planner.ValidateScopeAsync(
            new ValidateScopeCommand(_fixture.ConnectionString, scope, "insert"),
            CancellationToken.None);

        Assert.False(validateResult.IsValid);
        Assert.Single(validateResult.Errors);
        Assert.Contains(lookupName, validateResult.Errors[0]);
        Assert.Contains("no non-null values", validateResult.Errors[0]);

        // The dependent table must not have been touched.
        var count = (int)(await _fixture.ExecuteScalarAsync(
            $"SELECT COUNT(*) FROM dbo.{ordersName}"))!;
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ExternalRoot_AllNullColumnFailsFastDuringValidation()
    {
        // Table has rows but the source column is entirely NULL — same outcome
        // as an empty table: validation must fail fast.
        var lookupName = "TestExtLookupNulls_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName = "TestExtOrdersNullSrc_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{lookupName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            INSERT INTO dbo.{lookupName} (Code) VALUES (NULL), (NULL), (NULL)
            """);
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                LookupCode NVARCHAR(20) NOT NULL
            )
            """);

        var scope = new ScopeConfig(
            schemaFilter: ["dbo"],
            tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
            rowsPerTable: 5,
            seed: 42,
            locale: "en",
            customDependencies: [$"dbo.{lookupName}.Code|dbo.{ordersName}.LookupCode"]);

        var planner = new DataGenerationPlanner();
        var validateResult = await planner.ValidateScopeAsync(
            new ValidateScopeCommand(_fixture.ConnectionString, scope, "insert"),
            CancellationToken.None);

        Assert.False(validateResult.IsValid);
        Assert.Single(validateResult.Errors);
        Assert.Contains("no non-null values", validateResult.Errors[0]);

        var count = (int)(await _fixture.ExecuteScalarAsync(
            $"SELECT COUNT(*) FROM dbo.{ordersName}"))!;
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ExternalRoot_MultipleDependentsShareSameSource()
    {
        // Two scoped tables both depend on the same external column. Verify
        // that values from the live source propagate into BOTH dependents
        // (the streamer is shared per (table, column) key).
        var lookupName = "TestExtLookupShare_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName = "TestExtOrdersShare_" + Guid.NewGuid().ToString("N")[..8];
        var auditName = "TestExtAuditShare_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{lookupName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            );
            INSERT INTO dbo.{lookupName} (Code)
            VALUES ('ALPHA'), ('BETA'), ('GAMMA'), ('DELTA');

            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                LookupCode NVARCHAR(20) NOT NULL
            );
            CREATE TABLE dbo.{auditName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                LookupCode NVARCHAR(20) NOT NULL
            );
            """);

        var scope = new ScopeConfig(
            schemaFilter: ["dbo"],
            tablesToInclude:
            [
                new TableScope { Table = $"dbo.{ordersName}" },
                new TableScope { Table = $"dbo.{auditName}" }
            ],
            rowsPerTable: 20,
            seed: 42,
            locale: "en",
            customDependencies:
            [
                $"dbo.{lookupName}.Code|dbo.{ordersName}.LookupCode",
                $"dbo.{lookupName}.Code|dbo.{auditName}.LookupCode"
            ]);

        var planner = new DataGenerationPlanner();
        var executor = new DataGenerationExecutor();
        var orchestrator = new GeneratorOrchestrator(
            _fixture.ConnectionString, scope, planner, executor);

        await orchestrator.RunDirectAsync("insert");

        var ordersRows = await _fixture.ExecuteQueryAsync(
            $"SELECT LookupCode FROM dbo.{ordersName}");
        var auditRows = await _fixture.ExecuteQueryAsync(
            $"SELECT LookupCode FROM dbo.{auditName}");

        Assert.Equal(20, ordersRows.Count);
        Assert.Equal(20, auditRows.Count);

        var validCodes = new HashSet<string> { "ALPHA", "BETA", "GAMMA", "DELTA" };
        foreach (var row in ordersRows)
            Assert.Contains((string)row["LookupCode"]!, validCodes);
        foreach (var row in auditRows)
            Assert.Contains((string)row["LookupCode"]!, validCodes);
    }

    [Fact]
    public async Task ExternalRoot_MultipleDifferentExternalsCoexist()
    {
        // Two distinct external sources feeding two different scoped tables.
        // Each should populate independently from its own pool.
        var lookupAName = "TestExtLookupA_" + Guid.NewGuid().ToString("N")[..8];
        var lookupBName = "TestExtLookupB_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName  = "TestExtOrdersMulti_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{lookupAName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            );
            INSERT INTO dbo.{lookupAName} (Code) VALUES ('A1'), ('A2'), ('A3');

            CREATE TABLE dbo.{lookupBName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            );
            INSERT INTO dbo.{lookupBName} (Code) VALUES ('B1'), ('B2'), ('B3');

            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                CodeA NVARCHAR(20) NOT NULL,
                CodeB NVARCHAR(20) NOT NULL
            );
            """);

        var scope = new ScopeConfig(
            schemaFilter: ["dbo"],
            tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
            rowsPerTable: 30,
            seed: 42,
            locale: "en",
            customDependencies:
            [
                $"dbo.{lookupAName}.Code|dbo.{ordersName}.CodeA",
                $"dbo.{lookupBName}.Code|dbo.{ordersName}.CodeB"
            ]);

        var planner = new DataGenerationPlanner();
        var executor = new DataGenerationExecutor();
        var orchestrator = new GeneratorOrchestrator(
            _fixture.ConnectionString, scope, planner, executor);

        await orchestrator.RunDirectAsync("insert");

        var rows = await _fixture.ExecuteQueryAsync(
            $"SELECT CodeA, CodeB FROM dbo.{ordersName}");

        Assert.Equal(30, rows.Count);
        var validA = new HashSet<string> { "A1", "A2", "A3" };
        var validB = new HashSet<string> { "B1", "B2", "B3" };
        foreach (var row in rows)
        {
            Assert.Contains((string)row["CodeA"]!, validA);
            Assert.Contains((string)row["CodeB"]!, validB);
        }
    }

    [Fact]
    public async Task ExternalRoot_RejectsMultipleExternalsInSameGroup()
    {
        // Both sides external — validation must reject before any insert.
        var lookupAName = "TestExtRejectA_" + Guid.NewGuid().ToString("N")[..8];
        var lookupBName = "TestExtRejectB_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName  = "TestExtRejectOrders_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{lookupAName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            );
            INSERT INTO dbo.{lookupAName} (Code) VALUES ('X');

            CREATE TABLE dbo.{lookupBName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            );
            INSERT INTO dbo.{lookupBName} (Code) VALUES ('Y');

            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            );
            """);

        var scope = new ScopeConfig(
            schemaFilter: ["dbo"],
            tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
            rowsPerTable: 5,
            seed: 42,
            locale: "en",
            customDependencies:
            [
                $"dbo.{lookupAName}.Code|dbo.{lookupBName}.Code|dbo.{ordersName}.Code"
            ]);

        var planner = new DataGenerationPlanner();
        var validateResult = await planner.ValidateScopeAsync(
            new ValidateScopeCommand(_fixture.ConnectionString, scope, "insert"),
            CancellationToken.None);

        Assert.False(validateResult.IsValid);
        Assert.Contains(validateResult.Errors,
            e => e.Contains("multiple source-data providers")
                 && e.Contains("external root"));

        var count = (int)(await _fixture.ExecuteScalarAsync(
            $"SELECT COUNT(*) FROM dbo.{ordersName}"))!;
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ExternalRoot_SourceDeclaredSecond_StillResolvedByCascade()
    {
        // Verify end-to-end that order in the YAML doesn't matter: the
        // dependent column is declared first, the external source second.
        var lookupName = "TestExtCascade_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName = "TestExtCascadeOrders_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{lookupName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            );
            INSERT INTO dbo.{lookupName} (Code) VALUES ('one'), ('two'), ('three');

            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                LookupCode NVARCHAR(20) NOT NULL
            );
            """);

        var scope = new ScopeConfig(
            schemaFilter: ["dbo"],
            tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
            rowsPerTable: 10,
            seed: 42,
            locale: "en",
            // Dependent first, external source second — cascade should still
            // pick the external column as source.
            customDependencies: [$"dbo.{ordersName}.LookupCode|dbo.{lookupName}.Code"]);

        var planner = new DataGenerationPlanner();
        var executor = new DataGenerationExecutor();
        var orchestrator = new GeneratorOrchestrator(
            _fixture.ConnectionString, scope, planner, executor);

        await orchestrator.RunDirectAsync("insert");

        var rows = await _fixture.ExecuteQueryAsync(
            $"SELECT LookupCode FROM dbo.{ordersName}");

        Assert.Equal(10, rows.Count);
        var valid = new HashSet<string> { "one", "two", "three" };
        foreach (var row in rows)
            Assert.Contains((string)row["LookupCode"]!, valid);
    }
}
