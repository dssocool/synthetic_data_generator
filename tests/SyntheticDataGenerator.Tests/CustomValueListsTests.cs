using Microsoft.Extensions.Configuration;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;

namespace SyntheticDataGenerator.Tests;

public class CustomValueListsTests
{
    private static TableInfo MakeTable(string schema, string name, params string[] columns)
    {
        var table = new TableInfo
        {
            Schema = schema,
            TableName = name,
            Columns = [new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true }],
            PrimaryKeyColumns = ["Id"]
        };

        foreach (var col in columns)
            table.Columns.Add(new ColumnInfo { Name = col, SqlType = "nvarchar", MaxLength = 100 });

        return table;
    }

    /// <summary>
    /// Builds a tiny in-memory IConfiguration that exposes a CustomValueLists
    /// section in the same shape we expect the YAML loader to produce, so we
    /// can exercise <see cref="ScopeConfig.ParseCustomValueLists"/> end-to-end.
    /// </summary>
    private static IConfiguration BuildConfig(params (string Column, string File)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"CustomValueLists:{i}:Column"] = entries[i].Column;
            dict[$"CustomValueLists:{i}:File"] = entries[i].File;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static string WriteTempFile(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"cvl_test_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    #region Parsing tests

    [Fact]
    public void ParseCustomValueLists_StructuredEntries()
    {
        var config = BuildConfig(
            ("dbo.Lookup.Code", "/tmp/codes.txt"),
            ("dbo.Lookup.Name", "/tmp/names.txt"));

        var parsed = ScopeConfig.ParseCustomValueLists(config.GetSection("CustomValueLists"));

        Assert.Equal(2, parsed.Length);
        Assert.Equal("dbo.Lookup.Code", parsed[0].Column);
        Assert.Equal("/tmp/codes.txt", parsed[0].File);
        Assert.Equal("dbo.Lookup.Name", parsed[1].Column);
        Assert.Equal("/tmp/names.txt", parsed[1].File);
    }

    [Fact]
    public void ParseCustomValueLists_EmptySection_ReturnsEmptyArray()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var parsed = ScopeConfig.ParseCustomValueLists(config.GetSection("CustomValueLists"));

        Assert.Empty(parsed);
    }

    [Fact]
    public void ParseCustomValueLists_MissingFileFieldKeepsEntryWithEmptyFile()
    {
        // Parser tolerates the missing File field — validation later turns it
        // into a friendly error rather than a NullReferenceException here.
        var dict = new Dictionary<string, string?>
        {
            ["CustomValueLists:0:Column"] = "dbo.Lookup.Code"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var parsed = ScopeConfig.ParseCustomValueLists(config.GetSection("CustomValueLists"));

        Assert.Single(parsed);
        Assert.Equal("dbo.Lookup.Code", parsed[0].Column);
        Assert.Equal(string.Empty, parsed[0].File);
    }

    [Fact]
    public void ParseCustomValueLists_SkipsEntriesMissingColumn()
    {
        var dict = new Dictionary<string, string?>
        {
            ["CustomValueLists:0:File"] = "/tmp/orphan.txt"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var parsed = ScopeConfig.ParseCustomValueLists(config.GetSection("CustomValueLists"));

        Assert.Empty(parsed);
    }

    #endregion

    #region Validation tests

    [Fact]
    public void Validation_ValidEntry_FlagsExternalRootAndSetsValuesFile()
    {
        var orders = MakeTable("dbo", "Orders", "LookupCode");
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var file = WriteTempFile("ALPHA", "BETA", "GAMMA");
        try
        {
            var groups = ScopeConfig.ParseCustomDependencies(
                ["dbo.Lookup.Code|dbo.Orders.LookupCode"]);
            var customValueLists = new[]
            {
                new CustomValueList { Column = "dbo.Lookup.Code", File = file }
            };

            var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
                groups, scopedTables, allTables, columnScope: null, customValueLists);

            Assert.Empty(errors);
            var sourceCol = groups[0].Columns.Single(c => c.IsSource);
            Assert.Equal("dbo.Lookup", sourceCol.Table);
            Assert.True(sourceCol.IsExternalRoot);
            Assert.Equal(file, sourceCol.ValuesFile);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validation_FileDoesNotExist_Errors()
    {
        var orders = MakeTable("dbo", "Orders", "LookupCode");
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Lookup.Code|dbo.Orders.LookupCode"]);
        var customValueLists = new[]
        {
            new CustomValueList
            {
                Column = "dbo.Lookup.Code",
                File = "/this/path/does/not/exist_xyz_12345.txt"
            }
        };

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null, customValueLists);

        Assert.Contains(errors, e => e.Contains("does not exist")
                                     && e.Contains("dbo.Lookup.Code"));
    }

    [Fact]
    public void Validation_FileEmpty_Errors()
    {
        var orders = MakeTable("dbo", "Orders", "LookupCode");
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var emptyFile = WriteTempFile();
        try
        {
            var groups = ScopeConfig.ParseCustomDependencies(
                ["dbo.Lookup.Code|dbo.Orders.LookupCode"]);
            var customValueLists = new[]
            {
                new CustomValueList { Column = "dbo.Lookup.Code", File = emptyFile }
            };

            var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
                groups, scopedTables, allTables, columnScope: null, customValueLists);

            Assert.Contains(errors, e => e.Contains("is empty")
                                         && e.Contains("dbo.Lookup.Code"));
        }
        finally
        {
            File.Delete(emptyFile);
        }
    }

    [Fact]
    public void Validation_FileOnlyBlankLines_Errors()
    {
        var orders = MakeTable("dbo", "Orders", "LookupCode");
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var blankFile = WriteTempFile("", "  ", "\t");
        try
        {
            var groups = ScopeConfig.ParseCustomDependencies(
                ["dbo.Lookup.Code|dbo.Orders.LookupCode"]);
            var customValueLists = new[]
            {
                new CustomValueList { Column = "dbo.Lookup.Code", File = blankFile }
            };

            var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
                groups, scopedTables, allTables, columnScope: null, customValueLists);

            Assert.Contains(errors, e => e.Contains("is empty"));
        }
        finally
        {
            File.Delete(blankFile);
        }
    }

    [Fact]
    public void Validation_MissingFileField_Errors()
    {
        var orders = MakeTable("dbo", "Orders", "LookupCode");
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Lookup.Code|dbo.Orders.LookupCode"]);
        var customValueLists = new[]
        {
            new CustomValueList { Column = "dbo.Lookup.Code", File = "" }
        };

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null, customValueLists);

        Assert.Contains(errors, e => e.Contains("missing the File field"));
    }

    [Fact]
    public void Validation_MalformedColumn_Errors()
    {
        var orders = MakeTable("dbo", "Orders", "LookupCode");
        var allTables = new List<TableInfo> { orders };

        var file = WriteTempFile("a", "b");
        try
        {
            var groups = ScopeConfig.ParseCustomDependencies(
                ["dbo.Orders.LookupCode|dbo.Orders.LookupCode"]);
            var customValueLists = new[]
            {
                new CustomValueList { Column = "no_dot_here", File = file }
            };

            var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
                groups, allTables, allTables, columnScope: null, customValueLists);

            Assert.Contains(errors, e => e.Contains("schema.table.column"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validation_DuplicateColumnEntries_Errors()
    {
        var orders = MakeTable("dbo", "Orders", "LookupCode");
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var file1 = WriteTempFile("a");
        var file2 = WriteTempFile("b");
        try
        {
            var groups = ScopeConfig.ParseCustomDependencies(
                ["dbo.Lookup.Code|dbo.Orders.LookupCode"]);
            var customValueLists = new[]
            {
                new CustomValueList { Column = "dbo.Lookup.Code", File = file1 },
                new CustomValueList { Column = "dbo.Lookup.Code", File = file2 }
            };

            var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
                groups, scopedTables, allTables, columnScope: null, customValueLists);

            Assert.Contains(errors, e => e.Contains("duplicate entries"));
        }
        finally
        {
            File.Delete(file1);
            File.Delete(file2);
        }
    }

    [Fact]
    public void Validation_ColumnInsideScope_Errors()
    {
        // CustomValueLists columns must live OUTSIDE TablesToInclude — they're
        // treated as external roots. If the column is in scope it's a config
        // mistake.
        var orders = MakeTable("dbo", "Orders", "LookupCode");
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        // Both Orders AND Lookup are in scope here.
        var scopedTables = new List<TableInfo> { orders, lookup };

        var file = WriteTempFile("a", "b");
        try
        {
            var groups = ScopeConfig.ParseCustomDependencies(
                ["dbo.Lookup.Code|dbo.Orders.LookupCode"]);
            var customValueLists = new[]
            {
                new CustomValueList { Column = "dbo.Lookup.Code", File = file }
            };

            var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
                groups, scopedTables, allTables, columnScope: null, customValueLists);

            Assert.Contains(errors, e => e.Contains("must be outside")
                                         && e.Contains("dbo.Lookup")
                                         && e.Contains("Code"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validation_UnreferencedEntry_Errors()
    {
        // CustomValueLists entry that no CustomDependencies group mentions is
        // dead config — must surface as an error rather than silently ignored.
        var orders = MakeTable("dbo", "Orders", "LookupCode");
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var file = WriteTempFile("a");
        try
        {
            // Note: NO CustomDependencies group references Lookup.Code.
            var groups = new List<CustomDependencyGroup>();
            var customValueLists = new[]
            {
                new CustomValueList { Column = "dbo.Lookup.Code", File = file }
            };

            var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
                groups, scopedTables, allTables, columnScope: null, customValueLists);

            Assert.Contains(errors, e =>
                e.Contains("not referenced by any CustomDependencies group"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validation_ValueListWinsOverPkInSourceResolution()
    {
        // The value-list column is flagged IsExternalRoot=true, so the existing
        // cascade picks it as the group source (Tier 1 External). We declare a
        // PK candidate and the value-list column second to confirm position
        // does not influence the choice.
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders", PrimaryKeyColumns = ["OrderId"],
            Columns =
            [
                new ColumnInfo { Name = "OrderId", SqlType = "int", IsPrimaryKey = true },
                new ColumnInfo { Name = "LookupCode", SqlType = "nvarchar", MaxLength = 20 }
            ]
        };
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var file = WriteTempFile("ALPHA", "BETA");
        try
        {
            // Declare PK first; value-list-backed external second.
            var groups = ScopeConfig.ParseCustomDependencies(
                ["dbo.Orders.OrderId|dbo.Orders.LookupCode|dbo.Lookup.Code"]);
            var customValueLists = new[]
            {
                new CustomValueList { Column = "dbo.Lookup.Code", File = file }
            };

            var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
                groups, scopedTables, allTables, columnScope: null, customValueLists);

            Assert.Empty(errors);
            var source = groups[0].Columns.Single(c => c.IsSource);
            Assert.Equal("dbo.Lookup", source.Table);
            Assert.Equal("Code", source.Column);
            Assert.True(source.IsExternalRoot);
            Assert.Equal(file, source.ValuesFile);
        }
        finally
        {
            File.Delete(file);
        }
    }

    #endregion

    #region Plan emission tests

    [Fact]
    public void PlanGenerator_EmitsValuesFileInCustomDependencyArgs()
    {
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders", PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true, IsIdentity = true },
                new ColumnInfo { Name = "LookupCode", SqlType = "nvarchar", MaxLength = 20 }
            ]
        };

        var customDeps = new List<CustomDependencyGroup>
        {
            new()
            {
                Columns =
                [
                    new CustomColumnRef
                    {
                        Table = "dbo.Lookup",
                        Column = "Code",
                        IsExternalRoot = true,
                        IsSource = true,
                        ValuesFile = "/tmp/values.txt"
                    },
                    new CustomColumnRef
                    {
                        Table = "dbo.Orders",
                        Column = "LookupCode"
                    }
                ]
            }
        };

        var plan = new PlanGenerator().Generate(
            sortedTables: [orders],
            selfReferencingTables: new HashSet<string>(),
            defaultRowCount: 5,
            seed: 42,
            customDependencies: customDeps);

        var lookupColPlan = plan.Tables
            .Single(t => t.Table == "dbo.Orders")
            .Columns.Single(c => c.Name == "LookupCode");

        Assert.Equal("customDependency", lookupColPlan.Generator);
        Assert.True(lookupColPlan.GeneratorArgs.ContainsKey("valuesFile"));
        Assert.Equal("/tmp/values.txt", lookupColPlan.GeneratorArgs["valuesFile"]);
        Assert.Equal(true, lookupColPlan.GeneratorArgs["isExternal"]);
    }

    [Fact]
    public void PlanGenerator_OmitsValuesFileWhenNotSet()
    {
        // Plain external root (no value-list backing) should NOT emit a
        // valuesFile arg — the runtime branches on its presence.
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders", PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true, IsIdentity = true },
                new ColumnInfo { Name = "LookupCode", SqlType = "nvarchar", MaxLength = 20 }
            ]
        };

        var customDeps = new List<CustomDependencyGroup>
        {
            new()
            {
                Columns =
                [
                    new CustomColumnRef
                    {
                        Table = "dbo.Lookup",
                        Column = "Code",
                        IsExternalRoot = true,
                        IsSource = true
                    },
                    new CustomColumnRef
                    {
                        Table = "dbo.Orders",
                        Column = "LookupCode"
                    }
                ]
            }
        };

        var plan = new PlanGenerator().Generate(
            sortedTables: [orders],
            selfReferencingTables: new HashSet<string>(),
            defaultRowCount: 5,
            seed: 42,
            customDependencies: customDeps);

        var lookupColPlan = plan.Tables
            .Single(t => t.Table == "dbo.Orders")
            .Columns.Single(c => c.Name == "LookupCode");

        Assert.Equal("customDependency", lookupColPlan.Generator);
        Assert.False(lookupColPlan.GeneratorArgs.ContainsKey("valuesFile"));
    }

    #endregion

    #region ValueListSource tests

    [Fact]
    public void ValueListSource_PicksOnlyFromFileLines()
    {
        var file = WriteTempFile("RED", "GREEN", "BLUE", "", "  ");
        try
        {
            var src = new ValueListSource(file, new Random(42));
            var expected = new HashSet<string> { "RED", "GREEN", "BLUE" };

            for (var i = 0; i < 200; i++)
            {
                var v = (string)src.Pick();
                Assert.Contains(v, expected);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ValueListSource_ReachesEveryDistinctLine()
    {
        var file = WriteTempFile("a", "b", "c", "d", "e");
        try
        {
            var src = new ValueListSource(file, new Random(123));
            var seen = new HashSet<string>();
            for (var i = 0; i < 500; i++)
                seen.Add((string)src.Pick());

            Assert.Equal(new[] { "a", "b", "c", "d", "e" }.OrderBy(x => x),
                         seen.OrderBy(x => x));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ValueListSource_MissingFile_ThrowsOnFirstPick()
    {
        var src = new ValueListSource("/nope/no/such/file_xyz.txt");

        var ex = Assert.Throws<InvalidOperationException>(() => src.Pick());
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void ValueListSource_EmptyFile_ThrowsOnFirstPick()
    {
        var file = WriteTempFile();
        try
        {
            var src = new ValueListSource(file);

            var ex = Assert.Throws<InvalidOperationException>(() => src.Pick());
            Assert.Contains("is empty", ex.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    #endregion
}

[Collection("Database")]
public class CustomValueListsIntegrationTests
{
    private readonly DatabaseFixture _fixture;

    public CustomValueListsIntegrationTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private static string WriteTempFile(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"cvl_int_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public async Task ValueListRoot_PopulatesDependentFromFile()
    {
        // Mirror of ExternalRoot_PopulatesDependentFromLiveSource, but the
        // source's values come from a flat file via CustomValueLists rather
        // than the live DB. Note: the lookup table exists (validation
        // requires it) but is intentionally EMPTY — the file is the
        // source of truth.
        var lookupName = "TestCvlLookup_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName = "TestCvlOrders_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{lookupName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                LookupCode NVARCHAR(20) NOT NULL,
                Amount INT NOT NULL
            )
            """);

        var validCodes = new[] { "ALPHA", "BETA", "GAMMA", "DELTA", "EPSILON" };
        var file = WriteTempFile(validCodes);
        try
        {
            var scope = new ScopeConfig(
                schemaFilter: ["dbo"],
                tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
                rowsPerTable: 25,
                seed: 42,
                locale: "en",
                customDependencies: [$"dbo.{lookupName}.Code|dbo.{ordersName}.LookupCode"],
                customValueLists:
                [
                    new CustomValueList
                    {
                        Column = $"dbo.{lookupName}.Code",
                        File = file
                    }
                ]);

            var planner = new DataGenerationPlanner();
            var executor = new DataGenerationExecutor();
            var orchestrator = new GeneratorOrchestrator(
                _fixture.ConnectionString, scope, planner, executor);

            await orchestrator.RunDirectAsync("insert");

            var rows = await _fixture.ExecuteQueryAsync(
                $"SELECT LookupCode FROM dbo.{ordersName}");

            Assert.Equal(25, rows.Count);
            var validSet = new HashSet<string>(validCodes);
            foreach (var row in rows)
            {
                var code = (string)row["LookupCode"]!;
                Assert.Contains(code, validSet);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ValueListRoot_BypassesEmptySourceTableValidation()
    {
        // The plain-external-root path fails validation when the source table
        // is empty. With CustomValueLists, the file IS the source of truth so
        // the empty-table check must be skipped.
        var lookupName = "TestCvlEmpty_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName = "TestCvlEmptyOrders_" + Guid.NewGuid().ToString("N")[..8];

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

        var file = WriteTempFile("X", "Y", "Z");
        try
        {
            var scope = new ScopeConfig(
                schemaFilter: ["dbo"],
                tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
                rowsPerTable: 5,
                seed: 42,
                locale: "en",
                customDependencies: [$"dbo.{lookupName}.Code|dbo.{ordersName}.LookupCode"],
                customValueLists:
                [
                    new CustomValueList
                    {
                        Column = $"dbo.{lookupName}.Code",
                        File = file
                    }
                ]);

            var planner = new DataGenerationPlanner();
            var validateResult = await planner.ValidateScopeAsync(
                new ValidateScopeCommand(_fixture.ConnectionString, scope, "insert"),
                CancellationToken.None);

            Assert.True(validateResult.IsValid,
                "Validation must succeed despite an empty source table when " +
                "the column is backed by a CustomValueLists file. Errors: " +
                string.Join("; ", validateResult.Errors));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ValueListRoot_RejectsMissingDbColumn()
    {
        // Even though values come from a file, the column must still exist in
        // the DB schema (per design: "real_only" — mirrors external-root).
        var ordersName = "TestCvlMissingCol_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                LookupCode NVARCHAR(20) NOT NULL
            )
            """);

        var file = WriteTempFile("X");
        try
        {
            var scope = new ScopeConfig(
                schemaFilter: ["dbo"],
                tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
                rowsPerTable: 5,
                seed: 42,
                locale: "en",
                customDependencies:
                [
                    $"dbo.NoSuchTable_{Guid.NewGuid():N}.Code|dbo.{ordersName}.LookupCode"
                ],
                customValueLists:
                [
                    new CustomValueList
                    {
                        Column = $"dbo.NoSuchTable_{Guid.NewGuid():N}.Code",
                        File = file
                    }
                ]);

            var planner = new DataGenerationPlanner();
            var validateResult = await planner.ValidateScopeAsync(
                new ValidateScopeCommand(_fixture.ConnectionString, scope, "insert"),
                CancellationToken.None);

            Assert.False(validateResult.IsValid);
            Assert.Contains(validateResult.Errors,
                e => e.Contains("does not exist in the database"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
