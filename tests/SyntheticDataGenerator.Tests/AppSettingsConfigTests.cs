using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Tests;

/// <summary>
/// Pure-unit coverage for every supported option in <c>appsettings.yaml</c>.
/// These tests exercise the same <c>YAML -&gt; IConfiguration -&gt; ScopeConfig</c>
/// pipeline as <see cref="Program"/>, but with in-memory YAML strings so they
/// do not touch SQL Server. They double as a regression net for the README's
/// minimal and full configuration examples.
/// </summary>
public class AppSettingsConfigTests
{
    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Loads a YAML string through <c>NetEscapades.Configuration.Yaml</c> the
    /// same way <see cref="Program"/> loads <c>appsettings.yaml</c>.
    /// </summary>
    private static IConfiguration LoadYaml(string yaml)
    {
        var bytes = Encoding.UTF8.GetBytes(yaml);
        using var stream = new MemoryStream(bytes);
        return new ConfigurationBuilder().AddYamlStream(stream).Build();
    }

    /// <summary>
    /// Replays the <see cref="ScopeConfig"/> construction in
    /// <see cref="Program"/> lines 19-28 so the tests verify the full
    /// pipeline, not just the static parser methods in isolation.
    /// </summary>
    private static ScopeConfig BuildScopeFromYaml(string yaml)
    {
        var config = LoadYaml(yaml);
        return new ScopeConfig(
            tablesToInclude: ScopeConfig.ParseTablesToInclude(config.GetSection("TablesToInclude")),
            rowsPerTable: int.TryParse(config["RowsPerTable"], out var r) ? r : 100,
            seed: int.TryParse(config["Seed"], out var s) ? s : null,
            locale: config["Locale"] ?? "en",
            customDependencies: config.GetSection("CustomDependencies").Get<string[]>(),
            customDependencyBufferSize: int.TryParse(config["CustomDependencyBufferSize"], out var b) ? b : 10_000,
            customValueLists: ScopeConfig.ParseCustomValueLists(config.GetSection("CustomValueLists")),
            maxParallelTables: int.TryParse(config["MaxParallelTables"], out var p) ? p : null);
    }

    /// <summary>
    /// Replays the <c>ConnectionString</c> resolution in <see cref="Program"/>
    /// lines 10-17. Used by the <c>DatabaseName</c> override tests.
    /// </summary>
    private static string ResolveConnectionString(IConfiguration config)
    {
        var baseConnectionString = config["ConnectionString"]
            ?? throw new InvalidOperationException("ConnectionString is required in appsettings.yaml");

        var databaseName = config["DatabaseName"];
        return string.IsNullOrWhiteSpace(databaseName)
            ? baseConnectionString
            : new SqlConnectionStringBuilder(baseConnectionString)
                { InitialCatalog = databaseName }.ConnectionString;
    }

    // ──────────────────────────────────────────────
    // 1. Minimal config (the README's TL;DR)
    // ──────────────────────────────────────────────

    #region Minimal config

    [Fact]
    public void MinimalConfig_LoadsConnectionStringTablesAndRows()
    {
        // Verbatim from README.md TL;DR (lines 14-20). Only the database name
        // is generic ("YOUR_DB") because the test never opens the connection.
        const string yaml = """
            ConnectionString: "Server=localhost,1433;Database=YOUR_DB;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True;Encrypt=false;"
            TablesToInclude:
              - dbo.Users
              - dbo.Orders
            RowsPerTable: 100
            """;

        var config = LoadYaml(yaml);
        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(
            "Server=localhost,1433;Database=YOUR_DB;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True;Encrypt=false;",
            config["ConnectionString"]);

        Assert.Equal(2, scope.TablesToInclude.Length);
        Assert.Equal("dbo.Users", scope.TablesToInclude[0].Table);
        Assert.Null(scope.TablesToInclude[0].Columns);
        Assert.Equal("dbo.Orders", scope.TablesToInclude[1].Table);
        Assert.Null(scope.TablesToInclude[1].Columns);

        Assert.Equal(100, scope.RowsPerTable);

        Assert.Null(scope.Seed);
        Assert.Equal("en", scope.Locale);
        Assert.Empty(scope.CustomDependencies);
        Assert.Empty(scope.CustomValueLists);
        Assert.Equal(10_000, scope.CustomDependencyBufferSize);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount), scope.MaxParallelTables);
    }

    [Fact]
    public void MinimalConfig_MissingConnectionString_Throws()
    {
        const string yaml = """
            TablesToInclude:
              - dbo.Users
            RowsPerTable: 5
            """;

        var config = LoadYaml(yaml);

        var ex = Assert.Throws<InvalidOperationException>(() => ResolveConnectionString(config));
        Assert.Equal("ConnectionString is required in appsettings.yaml", ex.Message);
    }

    [Fact]
    public void MinimalConfig_OnlyConnectionString_BuildsEmptyScope()
    {
        // TablesToInclude is omitted — the parser returns an empty array and
        // lets the validator surface a friendlier error later in the pipeline.
        const string yaml = """
            ConnectionString: "Server=.;Database=master;Trusted_Connection=True;"
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Empty(scope.TablesToInclude);
        Assert.Equal(100, scope.RowsPerTable);
        Assert.Equal("en", scope.Locale);
    }

    #endregion

    // ──────────────────────────────────────────────
    // 2. TablesToInclude
    // ──────────────────────────────────────────────

    #region TablesToInclude

    [Fact]
    public void TablesToInclude_SimpleForm_AllColumnsNull()
    {
        const string yaml = """
            TablesToInclude:
              - dbo.Users
              - dbo.Orders
              - sales.Invoices
            """;

        var tables = ScopeConfig.ParseTablesToInclude(
            LoadYaml(yaml).GetSection("TablesToInclude"));

        Assert.Equal(3, tables.Length);
        Assert.Equal("dbo.Users", tables[0].Table);
        Assert.Equal("dbo.Orders", tables[1].Table);
        Assert.Equal("sales.Invoices", tables[2].Table);
        Assert.All(tables, t => Assert.Null(t.Columns));
    }

    [Fact]
    public void TablesToInclude_StructuredFormWithColumns_PopulatesColumnList()
    {
        const string yaml = """
            TablesToInclude:
              - Table: dbo.Users
                Columns:
                  - Id
                  - Email
                  - DisplayName
            """;

        var tables = ScopeConfig.ParseTablesToInclude(
            LoadYaml(yaml).GetSection("TablesToInclude"));

        var entry = Assert.Single(tables);
        Assert.Equal("dbo.Users", entry.Table);
        Assert.NotNull(entry.Columns);
        Assert.Equal(["Id", "Email", "DisplayName"], entry.Columns);
    }

    [Fact]
    public void TablesToInclude_MixedSimpleAndStructured()
    {
        const string yaml = """
            TablesToInclude:
              - dbo.Users
              - Table: dbo.Orders
                Columns:
                  - Id
                  - UserId
              - dbo.Products
            """;

        var tables = ScopeConfig.ParseTablesToInclude(
            LoadYaml(yaml).GetSection("TablesToInclude"));

        Assert.Equal(3, tables.Length);

        Assert.Equal("dbo.Users", tables[0].Table);
        Assert.Null(tables[0].Columns);

        Assert.Equal("dbo.Orders", tables[1].Table);
        Assert.NotNull(tables[1].Columns);
        Assert.Equal(["Id", "UserId"], tables[1].Columns);

        Assert.Equal("dbo.Products", tables[2].Table);
        Assert.Null(tables[2].Columns);
    }

    [Fact]
    public void TablesToInclude_EmptyList_ReturnsEmptyArray()
    {
        const string yaml = """
            TablesToInclude: []
            """;

        var tables = ScopeConfig.ParseTablesToInclude(
            LoadYaml(yaml).GetSection("TablesToInclude"));

        Assert.Empty(tables);
    }

    [Fact]
    public void TablesToInclude_StructuredEntryWithoutColumns_KeepsColumnsNull()
    {
        // Structured form with only a Table: key — Columns: omitted entirely.
        const string yaml = """
            TablesToInclude:
              - Table: dbo.Users
            """;

        var tables = ScopeConfig.ParseTablesToInclude(
            LoadYaml(yaml).GetSection("TablesToInclude"));

        var entry = Assert.Single(tables);
        Assert.Equal("dbo.Users", entry.Table);
        Assert.Null(entry.Columns);
    }

    [Fact]
    public void BuildColumnScope_AllSimpleEntries_ReturnsNull()
    {
        var scope = new ScopeConfig(
            tablesToInclude:
            [
                new TableScope { Table = "dbo.Users" },
                new TableScope { Table = "dbo.Orders" }
            ],
            rowsPerTable: 10,
            seed: null,
            locale: "en");

        Assert.Null(scope.BuildColumnScope());
    }

    [Fact]
    public void BuildColumnScope_StructuredEntries_ReturnsCaseInsensitiveDict()
    {
        var scope = new ScopeConfig(
            tablesToInclude:
            [
                new TableScope { Table = "dbo.Users", Columns = ["Id", "Email"] },
                new TableScope { Table = "dbo.Orders" },
                new TableScope { Table = "dbo.Products", Columns = ["Sku"] }
            ],
            rowsPerTable: 10,
            seed: null,
            locale: "en");

        var columnScope = scope.BuildColumnScope();

        Assert.NotNull(columnScope);
        Assert.Equal(2, columnScope.Count);

        Assert.True(columnScope.ContainsKey("DBO.users"));
        Assert.Contains("EMAIL", columnScope["dbo.Users"]);
        Assert.Contains("id", columnScope["dbo.Users"]);
        Assert.Contains("SKU", columnScope["dbo.Products"]);

        Assert.False(columnScope.ContainsKey("dbo.Orders"));
    }

    [Fact]
    public void GetIncludeTableNames_IsCaseInsensitive()
    {
        var scope = new ScopeConfig(
            tablesToInclude:
            [
                new TableScope { Table = "dbo.Users" },
                new TableScope { Table = "dbo.Orders" }
            ],
            rowsPerTable: 10,
            seed: null,
            locale: "en");

        var names = scope.GetIncludeTableNames();

        Assert.Equal(2, names.Count);
        Assert.Contains("DBO.USERS", names);
        Assert.Contains("dbo.orders", names);
        Assert.DoesNotContain("dbo.Products", names);
    }

    #endregion

    // ──────────────────────────────────────────────
    // 4. Numeric / string defaults from Program.cs
    // ──────────────────────────────────────────────

    #region Defaults

    [Fact]
    public void RowsPerTable_Missing_DefaultsTo100()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(100, scope.RowsPerTable);
    }

    [Fact]
    public void RowsPerTable_NonNumeric_DefaultsTo100()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            RowsPerTable: lots
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(100, scope.RowsPerTable);
    }

    [Fact]
    public void RowsPerTable_Numeric_IsHonored()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            RowsPerTable: 250
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(250, scope.RowsPerTable);
    }

    [Fact]
    public void Seed_Missing_IsNull()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Null(scope.Seed);
    }

    [Fact]
    public void Seed_Numeric_IsHonored()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            Seed: 12345
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(12345, scope.Seed);
    }

    [Fact]
    public void Locale_Missing_DefaultsToEn()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal("en", scope.Locale);
    }

    [Fact]
    public void Locale_Custom_IsHonored()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            Locale: fr
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal("fr", scope.Locale);
    }

    [Fact]
    public void CustomDependencyBufferSize_Missing_DefaultsTo10000()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(10_000, scope.CustomDependencyBufferSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void CustomDependencyBufferSize_ZeroOrNegative_FallsBackTo10000(int value)
    {
        var yaml = $"""
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            CustomDependencyBufferSize: {value}
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(10_000, scope.CustomDependencyBufferSize);
    }

    [Fact]
    public void CustomDependencyBufferSize_Positive_IsHonored()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            CustomDependencyBufferSize: 250
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(250, scope.CustomDependencyBufferSize);
    }

    [Fact]
    public void MaxParallelTables_Missing_DefaultsToProcessorCount()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(Math.Max(1, Environment.ProcessorCount), scope.MaxParallelTables);
    }

    [Fact]
    public void MaxParallelTables_Numeric_IsHonored()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            MaxParallelTables: 4
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(4, scope.MaxParallelTables);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50)]
    public void MaxParallelTables_ZeroOrNegative_FallsBackToProcessorCount(int value)
    {
        var yaml = $"""
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Users
            MaxParallelTables: {value}
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(Math.Max(1, Environment.ProcessorCount), scope.MaxParallelTables);
    }

    #endregion

    // ──────────────────────────────────────────────
    // 5. CustomDependencies / CustomValueLists from real YAML
    // ──────────────────────────────────────────────

    #region CustomDependencies / CustomValueLists from YAML

    [Fact]
    public void CustomDependencies_FromYaml_ParsesPipeSeparatedGroups()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Orders
            CustomDependencies:
              - dbo.Lookup.Code|dbo.Orders.LookupCode
              - dbo.Products.CategoryId|dbo.Categories.Id|dbo.Inventory.CategoryId
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(2, scope.CustomDependencies.Length);
        Assert.Equal("dbo.Lookup.Code|dbo.Orders.LookupCode", scope.CustomDependencies[0]);
        Assert.Equal(
            "dbo.Products.CategoryId|dbo.Categories.Id|dbo.Inventory.CategoryId",
            scope.CustomDependencies[1]);

        var groups = ScopeConfig.ParseCustomDependencies(scope.CustomDependencies);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Columns.Count);
        Assert.Equal(3, groups[1].Columns.Count);
        Assert.Equal("dbo.Categories", groups[1].Columns[1].Table);
        Assert.Equal("Id", groups[1].Columns[1].Column);
    }

    [Fact]
    public void CustomValueLists_FromYaml_ParsesFileEntries()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Orders
            CustomValueLists:
              - Column: dbo.Lookup.Code
                File: ./values/lookup_codes.txt
            """;

        var scope = BuildScopeFromYaml(yaml);

        var entry = Assert.Single(scope.CustomValueLists);
        Assert.Equal("dbo.Lookup.Code", entry.Column);
        Assert.Equal("./values/lookup_codes.txt", entry.File);
        Assert.Null(entry.Values);
    }

    [Fact]
    public void CustomValueLists_FromYaml_ParsesInlineValuesEntries()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Orders
            CustomValueLists:
              - Column: dbo.Lookup.Region
                Values:
                  - APAC
                  - EMEA
                  - AMER
            """;

        var scope = BuildScopeFromYaml(yaml);

        var entry = Assert.Single(scope.CustomValueLists);
        Assert.Equal("dbo.Lookup.Region", entry.Column);
        Assert.Equal(string.Empty, entry.File);
        Assert.NotNull(entry.Values);
        Assert.Equal(["APAC", "EMEA", "AMER"], entry.Values);
    }

    [Fact]
    public void CustomValueLists_FromYaml_MixedFileAndInline()
    {
        const string yaml = """
            ConnectionString: "x"
            TablesToInclude:
              - dbo.Orders
            CustomValueLists:
              - Column: dbo.Lookup.Code
                File: ./values/lookup_codes.txt
              - Column: dbo.Lookup.Region
                Values:
                  - APAC
                  - EMEA
            """;

        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(2, scope.CustomValueLists.Length);

        Assert.Equal("dbo.Lookup.Code", scope.CustomValueLists[0].Column);
        Assert.Equal("./values/lookup_codes.txt", scope.CustomValueLists[0].File);
        Assert.Null(scope.CustomValueLists[0].Values);

        Assert.Equal("dbo.Lookup.Region", scope.CustomValueLists[1].Column);
        Assert.Equal(string.Empty, scope.CustomValueLists[1].File);
        Assert.NotNull(scope.CustomValueLists[1].Values);
        Assert.Equal(["APAC", "EMEA"], scope.CustomValueLists[1].Values);
    }

    #endregion

    // ──────────────────────────────────────────────
    // 6. Full config from the README
    // ──────────────────────────────────────────────

    #region Full config

    [Fact]
    public void FullConfig_FromReadmeExample_ParsesEveryOption()
    {
        // Mirrors the YAML in README.md "Configuration" section (lines 84-202),
        // minus comments. Asserts every documented option round-trips into
        // ScopeConfig with the documented value.
        const string yaml = """
            ConnectionString: Server=YOUR_SERVER;Trusted_Connection=True;TrustServerCertificate=True;
            DatabaseName: YOUR_DATABASE
            TablesToInclude:
              - dbo.Orders
              - dbo.Users
            RowsPerTable: 100
            Seed: 12345
            Locale: en
            CustomDependencies:
              - dbo.Lookup.Code|dbo.Orders.LookupCode
              - dbo.Products.CategoryId|dbo.Categories.Id|dbo.Inventory.CategoryId
            CustomDependencyBufferSize: 10000
            MaxParallelTables: 8
            CustomValueLists:
              - Column: dbo.Lookup.Code
                File: ./values/lookup_codes.txt
              - Column: dbo.Lookup.Region
                Values:
                  - APAC
                  - EMEA
                  - AMER
              - Column: dbo.Orders.Status
                Values:
                  - Pending
                  - Active
                  - Closed
            """;

        var config = LoadYaml(yaml);
        var scope = BuildScopeFromYaml(yaml);

        Assert.Equal(
            "Server=YOUR_SERVER;Trusted_Connection=True;TrustServerCertificate=True;",
            config["ConnectionString"]);
        Assert.Equal("YOUR_DATABASE", config["DatabaseName"]);

        Assert.Equal(2, scope.TablesToInclude.Length);
        Assert.Equal("dbo.Orders", scope.TablesToInclude[0].Table);
        Assert.Equal("dbo.Users", scope.TablesToInclude[1].Table);
        Assert.All(scope.TablesToInclude, t => Assert.Null(t.Columns));

        Assert.Equal(100, scope.RowsPerTable);
        Assert.Equal(12345, scope.Seed);
        Assert.Equal("en", scope.Locale);

        Assert.Equal(2, scope.CustomDependencies.Length);
        var groups = ScopeConfig.ParseCustomDependencies(scope.CustomDependencies);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Columns.Count);
        Assert.Equal(3, groups[1].Columns.Count);

        Assert.Equal(10_000, scope.CustomDependencyBufferSize);
        Assert.Equal(8, scope.MaxParallelTables);

        Assert.Equal(3, scope.CustomValueLists.Length);

        Assert.Equal("dbo.Lookup.Code", scope.CustomValueLists[0].Column);
        Assert.Equal("./values/lookup_codes.txt", scope.CustomValueLists[0].File);
        Assert.Null(scope.CustomValueLists[0].Values);

        Assert.Equal("dbo.Lookup.Region", scope.CustomValueLists[1].Column);
        Assert.Equal(string.Empty, scope.CustomValueLists[1].File);
        Assert.Equal(["APAC", "EMEA", "AMER"], scope.CustomValueLists[1].Values);

        Assert.Equal("dbo.Orders.Status", scope.CustomValueLists[2].Column);
        Assert.Equal(string.Empty, scope.CustomValueLists[2].File);
        Assert.Equal(["Pending", "Active", "Closed"], scope.CustomValueLists[2].Values);
    }

    #endregion

    // ──────────────────────────────────────────────
    // 7. DatabaseName override
    // ──────────────────────────────────────────────

    #region DatabaseName override

    [Fact]
    public void DatabaseName_OverridesInitialCatalog_WhenSet()
    {
        const string yaml = """
            ConnectionString: "Server=localhost,1433;Database=master;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True;Encrypt=false;"
            DatabaseName: AnalyticsTest
            """;

        var resolved = ResolveConnectionString(LoadYaml(yaml));

        var builder = new SqlConnectionStringBuilder(resolved);
        Assert.Equal("AnalyticsTest", builder.InitialCatalog);
        Assert.Equal("localhost,1433", builder.DataSource);
        Assert.Equal("sa", builder.UserID);
        Assert.True(builder.TrustServerCertificate);
    }

    [Fact]
    public void DatabaseName_Missing_LeavesConnectionStringUntouched()
    {
        const string original =
            "Server=localhost,1433;Database=master;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True;Encrypt=false;";
        var yaml = $"""
            ConnectionString: "{original}"
            """;

        var resolved = ResolveConnectionString(LoadYaml(yaml));

        Assert.Equal(original, resolved);
    }

    [Fact]
    public void DatabaseName_WhitespaceOnly_LeavesConnectionStringUntouched()
    {
        const string original =
            "Server=localhost,1433;Database=master;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True;Encrypt=false;";
        var yaml = $"""
            ConnectionString: "{original}"
            DatabaseName: "   "
            """;

        var resolved = ResolveConnectionString(LoadYaml(yaml));

        Assert.Equal(original, resolved);
    }

    #endregion
}
