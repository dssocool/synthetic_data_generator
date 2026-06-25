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
        // Both File and Values omitted -> validator surfaces the "must specify
        // either File or Values" rule (since Values is the new alternative
        // source).
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

        Assert.Contains(errors, e => e.Contains("must specify either File or Values"));
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
    public void Validation_InScopeCvlColumnInGroup_UsedAsSource()
    {
        // CustomValueLists columns may now live IN-SCOPE. When such a column
        // is referenced by a CustomDependencies group, the value list backs
        // the column itself AND its dependents — the cascade must pick the
        // value-list column as the source (Tier 1: ValueList) regardless of
        // whether the column is also a PK on the in-scope side.
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

            Assert.Empty(errors);
            var source = groups[0].Columns.Single(c => c.IsSource);
            Assert.Equal("dbo.Lookup", source.Table);
            Assert.Equal("Code", source.Column);
            // In-scope CVL columns are NOT external roots.
            Assert.False(source.IsExternalRoot);
            Assert.Equal(file, source.ValuesFile);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validation_StandaloneCvlOnInScopeColumn_Succeeds()
    {
        // A CustomValueLists entry that no CustomDependencies group references
        // is now valid AS LONG AS the column is in scope: the planner just
        // generates that column directly from the list.
        var orders = MakeTable("dbo", "Orders", "Status");
        var allTables = new List<TableInfo> { orders };
        var scopedTables = new List<TableInfo> { orders };

        // No CustomDependencies group references the column.
        var groups = new List<CustomDependencyGroup>();
        var customValueLists = new[]
        {
            new CustomValueList
            {
                Column = "dbo.Orders.Status",
                Values = ["Pending", "Active", "Closed"]
            }
        };

        var standalone = new Dictionary<string, DataGenerationPlanner.ValueListEntry>(
            StringComparer.OrdinalIgnoreCase);
        var groupErrors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null,
            customValueLists, standalone);
        Assert.Empty(groupErrors);

        var standaloneErrors = DataGenerationPlanner.CollectStandaloneValueListErrors(
            standalone, scopedTables, columnScope: null);
        Assert.Empty(standaloneErrors);

        // The entry survives to the planner-facing lookup.
        Assert.True(standalone.ContainsKey("dbo.Orders.Status"));
        var entry = standalone["dbo.Orders.Status"];
        Assert.Null(entry.File);
        Assert.NotNull(entry.Values);
        Assert.Equal(new[] { "Pending", "Active", "Closed" }, entry.Values);
    }

    [Fact]
    public void Validation_StandaloneCvlOnOutOfScopeColumn_Errors()
    {
        // A standalone CustomValueLists entry on a column that is NOT in
        // TablesToInclude has no way to be applied — fail fast with a
        // descriptive error.
        var orders = MakeTable("dbo", "Orders", "LookupCode");
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var groups = new List<CustomDependencyGroup>();
        var customValueLists = new[]
        {
            new CustomValueList
            {
                Column = "dbo.Lookup.Code",
                Values = ["X", "Y"]
            }
        };

        var standalone = new Dictionary<string, DataGenerationPlanner.ValueListEntry>(
            StringComparer.OrdinalIgnoreCase);
        var groupErrors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null,
            customValueLists, standalone);
        Assert.Empty(groupErrors);

        var standaloneErrors = DataGenerationPlanner.CollectStandaloneValueListErrors(
            standalone, scopedTables, columnScope: null);

        Assert.Contains(standaloneErrors, e =>
            e.Contains("dbo.Lookup")
            && e.Contains("Code")
            && e.Contains("not in scope"));
        // Out-of-scope entry is removed from the lookup so the planner doesn't try to apply it.
        Assert.False(standalone.ContainsKey("dbo.Lookup.Code"));
    }

    [Fact]
    public void Validation_StandaloneCvlOnColumnExcludedFromColumnsFilter_Errors()
    {
        // The Orders table is in scope, but its Columns filter excludes 'Status'.
        // A standalone CustomValueLists entry on Status must therefore fail.
        var orders = MakeTable("dbo", "Orders", "Status", "Other");
        var allTables = new List<TableInfo> { orders };
        var scopedTables = new List<TableInfo> { orders };

        var columnScope = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Orders"] = new(StringComparer.OrdinalIgnoreCase) { "Other" }
        };

        var groups = new List<CustomDependencyGroup>();
        var customValueLists = new[]
        {
            new CustomValueList { Column = "dbo.Orders.Status", Values = ["A", "B"] }
        };

        var standalone = new Dictionary<string, DataGenerationPlanner.ValueListEntry>(
            StringComparer.OrdinalIgnoreCase);
        var groupErrors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope, customValueLists, standalone);
        Assert.Empty(groupErrors);

        var standaloneErrors = DataGenerationPlanner.CollectStandaloneValueListErrors(
            standalone, scopedTables, columnScope);

        Assert.Contains(standaloneErrors, e =>
            e.Contains("dbo.Orders")
            && e.Contains("Status")
            && e.Contains("not in scope"));
    }

    [Fact]
    public void Validation_GroupWithExternalAndCvl_Errors()
    {
        // A group containing BOTH an external column without value-list backing
        // (data would stream from the live DB) AND another column with a
        // CustomValueLists entry has two source-data providers — fail fast.
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var lookup = MakeTable("dbo", "Lookup", "Region");
        var areas = MakeTable("dbo", "Areas", "Code");
        var allTables = new List<TableInfo> { orders, lookup, areas };
        // Only Orders is in scope; both Lookup and Areas are external.
        var scopedTables = new List<TableInfo> { orders };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Lookup.Region|dbo.Areas.Code|dbo.Orders.RegionCode"]);
        var customValueLists = new[]
        {
            new CustomValueList { Column = "dbo.Lookup.Region", Values = ["APAC", "EMEA"] }
        };

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null, customValueLists);

        Assert.Single(errors);
        Assert.Contains("multiple source-data providers", errors[0]);
        Assert.Contains("CustomValueLists", errors[0]);
        Assert.Contains("external root", errors[0]);
        Assert.Contains("dbo.Lookup", errors[0]);
        Assert.Contains("dbo.Areas", errors[0]);
    }

    [Fact]
    public void Validation_GroupWithOnlyCvlSource_Succeeds()
    {
        // A single CVL-backed column among otherwise in-scope columns counts
        // as the lone source-data provider; other in-scope columns are
        // dependents.
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var stats = MakeTable("dbo", "Stats", "RegionCode");
        var lookup = MakeTable("dbo", "Lookup", "Region");
        var allTables = new List<TableInfo> { orders, stats, lookup };
        // Orders + Stats in scope; Lookup external (CVL-backed).
        var scopedTables = new List<TableInfo> { orders, stats };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.RegionCode|dbo.Stats.RegionCode|dbo.Lookup.Region"]);
        var customValueLists = new[]
        {
            new CustomValueList { Column = "dbo.Lookup.Region", Values = ["APAC", "EMEA"] }
        };

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null, customValueLists);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.Lookup", source.Table);
        Assert.Equal("Region", source.Column);
        Assert.True(source.IsExternalRoot);
        Assert.NotNull(source.Values);
        Assert.Equal(new[] { "APAC", "EMEA" }, source.Values);
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

    [Fact]
    public void ValueListSource_InMemoryCtor_PicksOnlyFromSuppliedValues()
    {
        var src = new ValueListSource(["RED", "GREEN", "BLUE"], new Random(42));
        var expected = new HashSet<string> { "RED", "GREEN", "BLUE" };

        for (var i = 0; i < 200; i++)
            Assert.Contains((string)src.Pick(), expected);

        Assert.Null(src.FilePath);
    }

    [Fact]
    public void ValueListSource_InMemoryCtor_ReachesEveryValue()
    {
        var src = new ValueListSource(["a", "b", "c", "d", "e"], new Random(123));
        var seen = new HashSet<string>();
        for (var i = 0; i < 500; i++)
            seen.Add((string)src.Pick());

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }.OrderBy(x => x),
                     seen.OrderBy(x => x));
    }

    [Fact]
    public void ValueListSource_InMemoryCtor_StripsBlanks()
    {
        // Blank/whitespace entries are ignored, same as for file-loaded values.
        var src = new ValueListSource(["", "X", "  ", "Y", "\t"], new Random(7));
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
            seen.Add((string)src.Pick());

        Assert.Equal(new[] { "X", "Y" }.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public void ValueListSource_InMemoryCtor_AllBlankList_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ValueListSource(["", "  ", "\t"]));
        Assert.Contains("inline values list is empty", ex.Message);
    }

    [Fact]
    public void ValueListSource_InMemoryCtor_EmptyList_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ValueListSource(Array.Empty<string>()));
        Assert.Contains("inline values list is empty", ex.Message);
    }

    #endregion

    #region Inline Values: parsing tests

    [Fact]
    public void ParseCustomValueLists_InlineValuesEntry()
    {
        var dict = new Dictionary<string, string?>
        {
            ["CustomValueLists:0:Column"] = "dbo.Lookup.Region",
            ["CustomValueLists:0:Values:0"] = "APAC",
            ["CustomValueLists:0:Values:1"] = "EMEA",
            ["CustomValueLists:0:Values:2"] = "AMER"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var parsed = ScopeConfig.ParseCustomValueLists(config.GetSection("CustomValueLists"));

        Assert.Single(parsed);
        Assert.Equal("dbo.Lookup.Region", parsed[0].Column);
        Assert.Equal(string.Empty, parsed[0].File);
        Assert.NotNull(parsed[0].Values);
        Assert.Equal(new[] { "APAC", "EMEA", "AMER" }, parsed[0].Values);
    }

    [Fact]
    public void ParseCustomValueLists_BothFileAndValuesPresent_ParsesBoth()
    {
        // Parser does not enforce the exclusivity rule — that is the
        // validator's job. We just confirm both fields make it through so the
        // validator can produce a friendly error.
        var dict = new Dictionary<string, string?>
        {
            ["CustomValueLists:0:Column"] = "dbo.Lookup.Code",
            ["CustomValueLists:0:File"] = "/tmp/codes.txt",
            ["CustomValueLists:0:Values:0"] = "X"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var parsed = ScopeConfig.ParseCustomValueLists(config.GetSection("CustomValueLists"));

        Assert.Single(parsed);
        Assert.Equal("/tmp/codes.txt", parsed[0].File);
        Assert.Equal(new[] { "X" }, parsed[0].Values);
    }

    [Fact]
    public void ParseCustomValueLists_MixedFileAndInlineEntries()
    {
        var dict = new Dictionary<string, string?>
        {
            ["CustomValueLists:0:Column"] = "dbo.Lookup.Code",
            ["CustomValueLists:0:File"] = "/tmp/codes.txt",
            ["CustomValueLists:1:Column"] = "dbo.Lookup.Region",
            ["CustomValueLists:1:Values:0"] = "APAC",
            ["CustomValueLists:1:Values:1"] = "EMEA"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var parsed = ScopeConfig.ParseCustomValueLists(config.GetSection("CustomValueLists"));

        Assert.Equal(2, parsed.Length);
        Assert.Equal("/tmp/codes.txt", parsed[0].File);
        Assert.Null(parsed[0].Values);
        Assert.Equal(string.Empty, parsed[1].File);
        Assert.Equal(new[] { "APAC", "EMEA" }, parsed[1].Values);
    }

    #endregion

    #region Inline Values: validation tests

    [Fact]
    public void Validation_InlineValues_ValidEntry_FlagsExternalRootAndSetsValues()
    {
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var lookup = MakeTable("dbo", "Lookup", "Region");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Lookup.Region|dbo.Orders.RegionCode"]);
        var customValueLists = new[]
        {
            new CustomValueList
            {
                Column = "dbo.Lookup.Region",
                Values = ["APAC", "EMEA", "AMER"]
            }
        };

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null, customValueLists);

        Assert.Empty(errors);
        var sourceCol = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.Lookup", sourceCol.Table);
        Assert.True(sourceCol.IsExternalRoot);
        Assert.Null(sourceCol.ValuesFile);
        Assert.NotNull(sourceCol.Values);
        Assert.Equal(new[] { "APAC", "EMEA", "AMER" }, sourceCol.Values);
    }

    [Fact]
    public void Validation_BothFileAndValues_Errors()
    {
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var lookup = MakeTable("dbo", "Lookup", "Region");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var file = WriteTempFile("FILE_VAL");
        try
        {
            var groups = ScopeConfig.ParseCustomDependencies(
                ["dbo.Lookup.Region|dbo.Orders.RegionCode"]);
            var customValueLists = new[]
            {
                new CustomValueList
                {
                    Column = "dbo.Lookup.Region",
                    File = file,
                    Values = ["INLINE_VAL"]
                }
            };

            var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
                groups, scopedTables, allTables, columnScope: null, customValueLists);

            Assert.Contains(errors, e =>
                e.Contains("must specify exactly one of File or Values"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validation_NeitherFileNorValues_Errors()
    {
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var lookup = MakeTable("dbo", "Lookup", "Region");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Lookup.Region|dbo.Orders.RegionCode"]);
        var customValueLists = new[]
        {
            new CustomValueList
            {
                Column = "dbo.Lookup.Region"
                // Both File and Values intentionally omitted.
            }
        };

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null, customValueLists);

        Assert.Contains(errors, e => e.Contains("must specify either File or Values"));
    }

    [Fact]
    public void Validation_InlineValues_AllBlank_Errors()
    {
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var lookup = MakeTable("dbo", "Lookup", "Region");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Lookup.Region|dbo.Orders.RegionCode"]);
        var customValueLists = new[]
        {
            new CustomValueList
            {
                Column = "dbo.Lookup.Region",
                Values = ["", "  ", "\t"]
            }
        };

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null, customValueLists);

        Assert.Contains(errors, e =>
            e.Contains("Values for column [dbo.Lookup.Region] is empty"));
    }

    [Fact]
    public void Validation_InlineValues_StripsBlanksFromColumnRefList()
    {
        // The validator should propagate a CLEANED list (no blanks) onto the
        // CustomColumnRef so the runtime never has to filter again.
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var lookup = MakeTable("dbo", "Lookup", "Region");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Lookup.Region|dbo.Orders.RegionCode"]);
        var customValueLists = new[]
        {
            new CustomValueList
            {
                Column = "dbo.Lookup.Region",
                Values = ["APAC", "", "EMEA", "  ", "AMER"]
            }
        };

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null, customValueLists);

        Assert.Empty(errors);
        var sourceCol = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal(new[] { "APAC", "EMEA", "AMER" }, sourceCol.Values);
    }

    [Fact]
    public void Validation_InlineValues_WinsOverPkInSourceResolution()
    {
        // Inline-values column is IsExternalRoot=true so the cascade picks it
        // (Tier 1 External) over a PK candidate, regardless of declaration order.
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders", PrimaryKeyColumns = ["OrderId"],
            Columns =
            [
                new ColumnInfo { Name = "OrderId", SqlType = "int", IsPrimaryKey = true },
                new ColumnInfo { Name = "RegionCode", SqlType = "nvarchar", MaxLength = 20 }
            ]
        };
        var lookup = MakeTable("dbo", "Lookup", "Region");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        // PK declared first; inline-values external second.
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.OrderId|dbo.Orders.RegionCode|dbo.Lookup.Region"]);
        var customValueLists = new[]
        {
            new CustomValueList
            {
                Column = "dbo.Lookup.Region",
                Values = ["APAC", "EMEA"]
            }
        };

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null, customValueLists);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.Lookup", source.Table);
        Assert.Equal("Region", source.Column);
        Assert.True(source.IsExternalRoot);
        Assert.Equal(new[] { "APAC", "EMEA" }, source.Values);
    }

    #endregion

    #region Inline Values: plan emission tests

    [Fact]
    public void PlanGenerator_EmitsInlineValuesArg_AndOmitsValuesFile()
    {
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders", PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true, IsIdentity = true },
                new ColumnInfo { Name = "RegionCode", SqlType = "nvarchar", MaxLength = 20 }
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
                        Column = "Region",
                        IsExternalRoot = true,
                        IsSource = true,
                        Values = ["APAC", "EMEA", "AMER"]
                    },
                    new CustomColumnRef
                    {
                        Table = "dbo.Orders",
                        Column = "RegionCode"
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

        var depColPlan = plan.Tables
            .Single(t => t.Table == "dbo.Orders")
            .Columns.Single(c => c.Name == "RegionCode");

        Assert.Equal("customDependency", depColPlan.Generator);
        Assert.False(depColPlan.GeneratorArgs.ContainsKey("valuesFile"));
        Assert.True(depColPlan.GeneratorArgs.ContainsKey("values"));
        var emitted = Assert.IsType<List<string>>(depColPlan.GeneratorArgs["values"]);
        Assert.Equal(new[] { "APAC", "EMEA", "AMER" }, emitted);
        Assert.Equal(true, depColPlan.GeneratorArgs["isExternal"]);
    }

    [Fact]
    public void PlanGenerator_StampsValueListGeneratorOnInScopeColumnFromStandaloneEntry()
    {
        // A standalone CustomValueLists entry (not in any CustomDependencies
        // group) on an in-scope column should produce a column plan whose
        // generator is "valueList" and whose generatorArgs carry the values.
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders", PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true, IsIdentity = true },
                new ColumnInfo { Name = "Status", SqlType = "nvarchar", MaxLength = 20 }
            ]
        };

        var standalone = new Dictionary<string, ValueListBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Orders.Status"] = new(File: null, Values: ["Pending", "Active", "Closed"])
        };

        var plan = new PlanGenerator().Generate(
            sortedTables: [orders],
            selfReferencingTables: new HashSet<string>(),
            defaultRowCount: 5,
            seed: 42,
            standaloneValueLists: standalone);

        var statusPlan = plan.Tables
            .Single(t => t.Table == "dbo.Orders")
            .Columns.Single(c => c.Name == "Status");

        Assert.Equal("valueList", statusPlan.Generator);
        Assert.True(statusPlan.GeneratorArgs.ContainsKey("values"));
        var emitted = Assert.IsType<List<string>>(statusPlan.GeneratorArgs["values"]);
        Assert.Equal(new[] { "Pending", "Active", "Closed" }, emitted);
    }

    [Fact]
    public void PlanGenerator_StampsValuesFileOnInScopeColumnFromStandaloneEntry()
    {
        // File-backed standalone CVL should emit a "valuesFile" generator arg.
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders", PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true, IsIdentity = true },
                new ColumnInfo { Name = "LookupCode", SqlType = "nvarchar", MaxLength = 20 }
            ]
        };

        var standalone = new Dictionary<string, ValueListBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Orders.LookupCode"] = new(File: "/tmp/codes.txt", Values: null)
        };

        var plan = new PlanGenerator().Generate(
            sortedTables: [orders],
            selfReferencingTables: new HashSet<string>(),
            defaultRowCount: 5,
            seed: 42,
            standaloneValueLists: standalone);

        var colPlan = plan.Tables
            .Single(t => t.Table == "dbo.Orders")
            .Columns.Single(c => c.Name == "LookupCode");

        Assert.Equal("valueList", colPlan.Generator);
        Assert.True(colPlan.GeneratorArgs.ContainsKey("valuesFile"));
        Assert.Equal("/tmp/codes.txt", colPlan.GeneratorArgs["valuesFile"]);
        Assert.False(colPlan.GeneratorArgs.ContainsKey("values"));
    }

    [Fact]
    public void PlanGenerator_StampsValueListOnInScopeGroupSource()
    {
        // When the source column of a CustomDependencies group is in-scope AND
        // backed by a CustomValueLists entry, the source column itself must use
        // the "valueList" generator. The dependents continue to use
        // "customDependency" and copy from the source's generated rows.
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders", PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true, IsIdentity = true },
                new ColumnInfo { Name = "RegionCode", SqlType = "nvarchar", MaxLength = 20 }
            ]
        };
        var stats = new TableInfo
        {
            Schema = "dbo", TableName = "Stats", PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true, IsIdentity = true },
                new ColumnInfo { Name = "RegionCode", SqlType = "nvarchar", MaxLength = 20 }
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
                        Table = "dbo.Orders", Column = "RegionCode",
                        IsSource = true,
                        Values = ["APAC", "EMEA", "AMER"]
                    },
                    new CustomColumnRef { Table = "dbo.Stats", Column = "RegionCode" }
                ]
            }
        };

        var plan = new PlanGenerator().Generate(
            sortedTables: [orders, stats],
            selfReferencingTables: new HashSet<string>(),
            defaultRowCount: 5,
            seed: 42,
            customDependencies: customDeps);

        var ordersRegion = plan.Tables
            .Single(t => t.Table == "dbo.Orders")
            .Columns.Single(c => c.Name == "RegionCode");
        Assert.Equal("valueList", ordersRegion.Generator);
        Assert.True(ordersRegion.GeneratorArgs.ContainsKey("values"));

        var statsRegion = plan.Tables
            .Single(t => t.Table == "dbo.Stats")
            .Columns.Single(c => c.Name == "RegionCode");
        Assert.Equal("customDependency", statsRegion.Generator);
        Assert.Equal("dbo.Orders", statsRegion.GeneratorArgs["sourceTable"]);
        Assert.Equal("RegionCode", statsRegion.GeneratorArgs["sourceColumn"]);
    }

    [Fact]
    public void ColumnValueGenerator_PicksOnlyFromInlineValuesArg()
    {
        // The runtime "valueList" generator must honor an inline `values` arg
        // even when no `valuesFile` is supplied. This is the round-trip path
        // for inline CVL data.
        var plan = new ColumnPlan
        {
            Name = "Status",
            SqlType = "nvarchar",
            MaxLength = 20,
            Generator = "valueList",
            GeneratorArgs = new Dictionary<string, object?>
            {
                ["values"] = new List<string> { "Pending", "Active", "Closed" }
            }
        };

        var gen = new ColumnValueGenerator(seed: 42);
        var seen = new HashSet<string>();
        var allowed = new HashSet<string> { "Pending", "Active", "Closed" };
        for (var i = 0; i < 100; i++)
        {
            var v = (string)gen.GenerateFromPlan(plan)!;
            Assert.Contains(v, allowed);
            seen.Add(v);
        }
        Assert.Equal(allowed.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task PlanGenerator_InlineValuesRoundTripThroughYaml()
    {
        // Verify the inline list survives a serialize/deserialize round trip
        // through plan.yaml AND that the executor's BuildCustomDepGroupsFromPlan
        // recovers it as a usable List<string>. This is the moment where
        // YamlDotNet swaps List<string> for List<object>, so the normalization
        // path matters.
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders", PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true, IsIdentity = true },
                new ColumnInfo { Name = "RegionCode", SqlType = "nvarchar", MaxLength = 20 }
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
                        Table = "dbo.Lookup", Column = "Region",
                        IsExternalRoot = true, IsSource = true,
                        Values = ["APAC", "EMEA", "AMER"]
                    },
                    new CustomColumnRef { Table = "dbo.Orders", Column = "RegionCode" }
                ]
            }
        };

        var plan = new PlanGenerator().Generate(
            sortedTables: [orders],
            selfReferencingTables: new HashSet<string>(),
            defaultRowCount: 5,
            seed: 42,
            customDependencies: customDeps);

        var tmpPath = Path.Combine(Path.GetTempPath(),
            $"cvl_plan_{Guid.NewGuid():N}.yaml");
        try
        {
            await new PlanGenerator().WritePlanAsync(plan, tmpPath);
            var roundTripped = await PlanGenerator.ReadPlanAsync(tmpPath);

            var depColPlan = roundTripped.Tables
                .Single(t => t.Table == "dbo.Orders")
                .Columns.Single(c => c.Name == "RegionCode");

            // Reuse the executor's normalization to confirm runtime hookup.
            var groups = DataInserter.BuildCustomDepGroupsFromPlan(
                roundTripped.Tables.Single(t => t.Table == "dbo.Orders").Columns);

            var dep = groups.Single(g => g.DependentColumn == "RegionCode");
            Assert.Equal("dbo.Lookup", dep.SourceTable);
            Assert.Equal("Region", dep.SourceColumn);
            Assert.True(dep.IsExternal);
            Assert.Null(dep.ValuesFile);
            Assert.NotNull(dep.Values);
            Assert.Equal(new[] { "APAC", "EMEA", "AMER" }, dep.Values);
        }
        finally
        {
            File.Delete(tmpPath);
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

    [Fact]
    public async Task StandaloneCvl_PopulatesInScopeColumnFromInlineValues()
    {
        // A standalone CustomValueLists entry on an in-scope column (no
        // CustomDependencies group references it) generates that column
        // directly from the inline list. End-to-end smoke test of the new
        // "valueList" generator path.
        var ordersName = "TestStandaloneCvlOrders_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Status NVARCHAR(20) NOT NULL,
                Amount INT NOT NULL
            )
            """);

        var validStatuses = new[] { "Pending", "Active", "Closed" };
        var scope = new ScopeConfig(
            tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
            rowsPerTable: 25,
            seed: 42,
            locale: "en",
            customValueLists:
            [
                new CustomValueList
                {
                    Column = $"dbo.{ordersName}.Status",
                    Values = validStatuses.ToList()
                }
            ]);

        var planner = new DataGenerationPlanner();
        var executor = new DataGenerationExecutor();
        var orchestrator = new GeneratorOrchestrator(
            _fixture.ConnectionString, scope, planner, executor);

        await orchestrator.RunDirectAsync("insert");

        var rows = await _fixture.ExecuteQueryAsync(
            $"SELECT Status FROM dbo.{ordersName}");

        Assert.Equal(25, rows.Count);
        var validSet = new HashSet<string>(validStatuses);
        foreach (var row in rows)
        {
            var status = (string)row["Status"]!;
            Assert.Contains(status, validSet);
        }
    }

    [Fact]
    public async Task StandaloneCvl_FailsFastWhenColumnNotInScope()
    {
        // A standalone CustomValueLists entry on a column whose table is NOT
        // in TablesToInclude must surface as a validation error before any
        // insert is attempted.
        var ordersName = "TestStandaloneCvlMissing_" + Guid.NewGuid().ToString("N")[..8];
        var lookupName = "TestStandaloneCvlLookup_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(20) NOT NULL
            );

            CREATE TABLE dbo.{lookupName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Region NVARCHAR(20) NOT NULL
            );
            """);

        var scope = new ScopeConfig(
            // Lookup table intentionally NOT in scope.
            tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
            rowsPerTable: 5,
            seed: 42,
            locale: "en",
            customValueLists:
            [
                new CustomValueList
                {
                    Column = $"dbo.{lookupName}.Region",
                    Values = ["APAC", "EMEA"]
                }
            ]);

        var planner = new DataGenerationPlanner();
        var validateResult = await planner.ValidateScopeAsync(
            new ValidateScopeCommand(_fixture.ConnectionString, scope, "insert"),
            CancellationToken.None);

        Assert.False(validateResult.IsValid);
        Assert.Contains(validateResult.Errors,
            e => e.Contains($"dbo.{lookupName}")
                 && e.Contains("Region")
                 && e.Contains("not in scope"));
    }

    [Fact]
    public async Task ValueListRoot_PopulatesDependentFromInlineValues()
    {
        // End-to-end mirror of ValueListRoot_PopulatesDependentFromFile, but
        // the source values are supplied inline via Values: instead of File:.
        // The lookup table exists (validation requires it) but is intentionally
        // EMPTY — the inline list is the source of truth.
        var lookupName = "TestCvlInlineLookup_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName = "TestCvlInlineOrders_" + Guid.NewGuid().ToString("N")[..8];

        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{lookupName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Region NVARCHAR(20) NOT NULL
            )
            """);
        await _fixture.ExecuteSqlAsync($"""
            CREATE TABLE dbo.{ordersName} (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                RegionCode NVARCHAR(20) NOT NULL,
                Amount INT NOT NULL
            )
            """);

        var validRegions = new[] { "APAC", "EMEA", "AMER", "LATAM" };
        var scope = new ScopeConfig(
            tablesToInclude: [new TableScope { Table = $"dbo.{ordersName}" }],
            rowsPerTable: 25,
            seed: 42,
            locale: "en",
            customDependencies:
            [
                $"dbo.{lookupName}.Region|dbo.{ordersName}.RegionCode"
            ],
            customValueLists:
            [
                new CustomValueList
                {
                    Column = $"dbo.{lookupName}.Region",
                    Values = validRegions.ToList()
                }
            ]);

        var planner = new DataGenerationPlanner();
        var executor = new DataGenerationExecutor();
        var orchestrator = new GeneratorOrchestrator(
            _fixture.ConnectionString, scope, planner, executor);

        await orchestrator.RunDirectAsync("insert");

        var rows = await _fixture.ExecuteQueryAsync(
            $"SELECT RegionCode FROM dbo.{ordersName}");

        Assert.Equal(25, rows.Count);
        var validSet = new HashSet<string>(validRegions);
        foreach (var row in rows)
        {
            var code = (string)row["RegionCode"]!;
            Assert.Contains(code, validSet);
        }
    }
}
