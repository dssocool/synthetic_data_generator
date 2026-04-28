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
}
