using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SyntheticDataGenerator.Tests;

public class CustomDependencyTests
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

    #region Parsing tests

    [Fact]
    public void ParseCustomDependencies_BasicGroup()
    {
        var raw = new[] { "dbo.Orders.RegionCode|dbo.Regions.Code|dbo.RegionStats.RegionCode" };
        var groups = ScopeConfig.ParseCustomDependencies(raw);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Columns.Count);
        Assert.Equal("dbo.Orders", groups[0].Columns[0].Table);
        Assert.Equal("RegionCode", groups[0].Columns[0].Column);
        Assert.Equal("dbo.Regions", groups[0].Columns[1].Table);
        Assert.Equal("Code", groups[0].Columns[1].Column);
        Assert.Equal("dbo.RegionStats", groups[0].Columns[2].Table);
        Assert.Equal("RegionCode", groups[0].Columns[2].Column);
    }

    [Fact]
    public void ParseCustomDependencies_MultipleGroups()
    {
        var raw = new[]
        {
            "dbo.Orders.RegionCode|dbo.Regions.Code",
            "dbo.Products.CategoryName|dbo.Categories.Name"
        };
        var groups = ScopeConfig.ParseCustomDependencies(raw);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Columns.Count);
        Assert.Equal(2, groups[1].Columns.Count);
        Assert.Equal("dbo.Products", groups[1].Columns[0].Table);
        Assert.Equal("CategoryName", groups[1].Columns[0].Column);
    }

    [Fact]
    public void ParseCustomDependencies_SkipsEmptyAndWhitespace()
    {
        var raw = new[] { "", "  ", "dbo.A.Col1|dbo.B.Col2" };
        var groups = ScopeConfig.ParseCustomDependencies(raw);

        Assert.Single(groups);
    }

    [Fact]
    public void ParseCustomDependencies_SkipsSingleColumnGroup()
    {
        var raw = new[] { "dbo.Orders.RegionCode" };
        var groups = ScopeConfig.ParseCustomDependencies(raw);

        Assert.Empty(groups);
    }

    [Fact]
    public void ParseCustomDependencies_HandlesSpacesAroundPipes()
    {
        var raw = new[] { "dbo.Orders.Col1 | dbo.Regions.Col2 | dbo.Stats.Col3" };
        var groups = ScopeConfig.ParseCustomDependencies(raw);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Columns.Count);
        Assert.Equal("dbo.Orders", groups[0].Columns[0].Table);
        Assert.Equal("Col1", groups[0].Columns[0].Column);
    }

    [Fact]
    public void ParseCustomDependencies_EmptyArray()
    {
        var groups = ScopeConfig.ParseCustomDependencies([]);
        Assert.Empty(groups);
    }

    #endregion

    #region Validation tests

    [Fact]
    public void CollectCustomDependencyErrors_NoErrorsWhenValid()
    {
        var tables = new List<TableInfo>
        {
            MakeTable("dbo", "Orders", "RegionCode"),
            MakeTable("dbo", "Regions", "Code")
        };
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Empty(errors);
        Assert.False(groups[0].Columns[0].IsExternalRoot);
    }

    [Fact]
    public void CollectCustomDependencyErrors_MissingTable()
    {
        var tables = new List<TableInfo>
        {
            MakeTable("dbo", "Orders", "RegionCode")
        };
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Single(errors);
        Assert.Contains("dbo.Regions", errors[0]);
        Assert.Contains("does not exist in the database", errors[0]);
    }

    [Fact]
    public void CollectCustomDependencyErrors_MissingColumn()
    {
        var tables = new List<TableInfo>
        {
            MakeTable("dbo", "Orders", "RegionCode"),
            MakeTable("dbo", "Regions")
        };
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Single(errors);
        Assert.Contains("Code", errors[0]);
        Assert.Contains("does not exist", errors[0]);
    }

    [Fact]
    public void CollectCustomDependencyErrors_MultipleMissing()
    {
        var tables = new List<TableInfo>
        {
            MakeTable("dbo", "Orders")
        };
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void CollectCustomDependencyErrors_AccumulatesAcrossGroups()
    {
        // Two groups, each broken in a different way. The validator must
        // collect every error rather than short-circuiting on the first.
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var regions = MakeTable("dbo", "Regions", "Code");
        var areas = MakeTable("dbo", "Areas", "Code");
        var allTables = new List<TableInfo> { orders, regions, areas };
        var scopedTables = new List<TableInfo> { orders };

        var groups = ScopeConfig.ParseCustomDependencies(
        [
            // Group 1: missing table.
            "dbo.NonExistent.Foo|dbo.Orders.RegionCode",
            // Group 2: multi-external (Regions + Areas both out-of-scope).
            "dbo.Regions.Code|dbo.Areas.Code|dbo.Orders.RegionCode"
        ]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("dbo.NonExistent")
                                     && e.Contains("does not exist in the database"));
        Assert.Contains(errors, e => e.Contains("multiple source-data providers"));
    }

    [Fact]
    public void CollectCustomDependencyErrors_GroupWithMissingColumn_DoesNotResolveSource()
    {
        // When a group has any unresolvable reference, source resolution
        // must be skipped entirely (no IsSource flag set on any column).
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var regions = MakeTable("dbo", "Regions", "Code");
        var allTables = new List<TableInfo> { orders, regions };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.NonExistentCol|dbo.Orders.RegionCode"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, allTables, allTables, columnScope: null);

        Assert.Single(errors);
        Assert.Contains("NonExistentCol", errors[0]);
        Assert.DoesNotContain(groups[0].Columns, c => c.IsSource);
    }

    [Fact]
    public void CollectCustomDependencyErrors_SingleColumnGroupIgnored()
    {
        // The parser already drops single-column groups, but the validator
        // is also defensive: it must not error or set IsSource on a degenerate
        // group. We construct one manually to exercise the validator's branch.
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var allTables = new List<TableInfo> { orders };

        var degenerate = new List<CustomDependencyGroup>
        {
            new() { Columns = [new CustomColumnRef { Table = "dbo.Orders", Column = "RegionCode" }] }
        };

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            degenerate, allTables, allTables, columnScope: null);

        Assert.Empty(errors);
        Assert.False(degenerate[0].Columns[0].IsSource);
    }

    [Fact]
    public void CollectCustomDependencyErrors_AllowsExternalRootTable()
    {
        // Orders is in scope, Regions is in the DB but NOT in Include.
        // Regions.Code becomes an external root.
        var allTables = new List<TableInfo>
        {
            MakeTable("dbo", "Orders", "RegionCode"),
            MakeTable("dbo", "Regions", "Code")
        };
        var scopedTables = new List<TableInfo>
        {
            allTables.First(t => t.TableName == "Orders")
        };
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null);

        Assert.Empty(errors);
        Assert.True(groups[0].Columns[0].IsExternalRoot,
            "Source column on a table outside scope must be flagged external.");
        Assert.False(groups[0].Columns[1].IsExternalRoot,
            "Dependent column inside scope must remain non-external.");
    }

    [Fact]
    public void CollectCustomDependencyErrors_AllowsExternalRootColumn()
    {
        // Both tables in scope, but Regions has a column-scope filter that
        // excludes 'Code'. That column is then considered an external root.
        var allTables = new List<TableInfo>
        {
            MakeTable("dbo", "Orders", "RegionCode"),
            MakeTable("dbo", "Regions", "Code", "Other")
        };
        var columnScope = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Regions"] = new(StringComparer.OrdinalIgnoreCase) { "Other" }
        };
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, allTables, allTables, columnScope);

        Assert.Empty(errors);
        Assert.True(groups[0].Columns[0].IsExternalRoot);
    }

    [Fact]
    public void CollectCustomDependencyErrors_RejectsMultipleExternals()
    {
        // Two external columns in the same group → fatal error.
        var allTables = new List<TableInfo>
        {
            MakeTable("dbo", "Orders", "RegionCode"),
            MakeTable("dbo", "Regions", "Code"),
            MakeTable("dbo", "Areas", "Code")
        };
        // Only Orders is in scope; both Regions and Areas are external.
        var scopedTables = new List<TableInfo>
        {
            allTables.First(t => t.TableName == "Orders")
        };
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Areas.Code|dbo.Orders.RegionCode"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null);

        Assert.Single(errors);
        Assert.Contains("multiple source-data providers", errors[0]);
        Assert.Contains("dbo.Regions", errors[0]);
        Assert.Contains("dbo.Areas", errors[0]);
        Assert.Contains("external root", errors[0]);
        Assert.Contains("At most one source-data provider is allowed per group", errors[0]);
    }

    [Fact]
    public void ResolveSource_ExternalWinsOverPk()
    {
        // Regions.Id is an out-of-scope PK; Orders.RegionCode is an in-scope
        // plain column. External wins (Tier 1).
        var allTables = new List<TableInfo>
        {
            MakeTable("dbo", "Orders", "RegionCode"),
            MakeTable("dbo", "Regions") // only the PK 'Id'
        };
        var scopedTables = new List<TableInfo>
        {
            allTables.First(t => t.TableName == "Orders")
        };
        // Declare the PK second so we know external (Regions.Id) didn't win
        // by virtue of being declared first.
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.RegionCode|dbo.Regions.Id"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.Regions", source.Table);
        Assert.Equal("Id", source.Column);
        Assert.True(source.IsExternalRoot);
    }

    [Fact]
    public void ResolveSource_PkWinsOverAutoGen()
    {
        // Orders has a non-identity PK 'OrderCode'; AuditLog has an identity
        // 'Id' that is NOT a PK. PK (Tier 2) wins over AutoGenerated (Tier 3).
        // The previous 2-column auto-correction would have picked the identity.
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders",
            PrimaryKeyColumns = ["OrderCode"],
            Columns =
            [
                new ColumnInfo { Name = "OrderCode", SqlType = "nvarchar", MaxLength = 20, IsPrimaryKey = true }
            ]
        };
        var auditLog = new TableInfo
        {
            Schema = "dbo", TableName = "AuditLog",
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsIdentity = true }
            ]
        };
        var tables = new List<TableInfo> { orders, auditLog };

        // Declare the identity column first to confirm cascade beats position.
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.AuditLog.Id|dbo.Orders.OrderCode"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.Orders", source.Table);
        Assert.Equal("OrderCode", source.Column);
    }

    [Fact]
    public void ResolveSource_AutoGenWinsOverUnique()
    {
        // No PK on either side. Customers.Id is identity; Orders.OrderCode
        // is unique-not-PK. AutoGen (Tier 3) wins over Unique (Tier 4).
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders",
            Columns =
            [
                new ColumnInfo { Name = "OrderCode", SqlType = "nvarchar", MaxLength = 20, IsUnique = true }
            ]
        };
        var customers = new TableInfo
        {
            Schema = "dbo", TableName = "Customers",
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsIdentity = true }
            ]
        };
        var tables = new List<TableInfo> { orders, customers };

        // Declare the unique column first; AutoGen should still win.
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.OrderCode|dbo.Customers.Id"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.Customers", source.Table);
    }

    [Fact]
    public void ResolveSource_UniqueWinsOverPlain()
    {
        // No PK, no auto-gen on either side. One side has IsUnique.
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders",
            Columns =
            [
                new ColumnInfo { Name = "RegionCode", SqlType = "nvarchar", MaxLength = 10 }
            ]
        };
        var regions = new TableInfo
        {
            Schema = "dbo", TableName = "Regions",
            Columns =
            [
                new ColumnInfo { Name = "Code", SqlType = "nvarchar", MaxLength = 10, IsUnique = true }
            ]
        };
        var tables = new List<TableInfo> { orders, regions };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.RegionCode|dbo.Regions.Code"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.Regions", source.Table);
    }

    [Fact]
    public void ResolveSource_AllPlainColumnsFallsBackToFirstDeclared()
    {
        // No tier matches → first declared wins.
        var a = new TableInfo
        {
            Schema = "dbo", TableName = "TableA",
            Columns =
            [
                new ColumnInfo { Name = "Col1", SqlType = "nvarchar", MaxLength = 10 }
            ]
        };
        var b = new TableInfo
        {
            Schema = "dbo", TableName = "TableB",
            Columns =
            [
                new ColumnInfo { Name = "Col1", SqlType = "nvarchar", MaxLength = 10 }
            ]
        };
        var tables = new List<TableInfo> { a, b };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.TableA.Col1|dbo.TableB.Col1"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.TableA", source.Table);
    }

    [Fact]
    public void ResolveSource_FirstDeclaredOnTieAtLowestTier()
    {
        // Both columns are PKs from different tables: cascade narrows but
        // doesn't pick a single winner; the final fallback (first declared)
        // applies, which by then is constrained to the PK candidates.
        var a = new TableInfo
        {
            Schema = "dbo", TableName = "TableA",
            PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true }
            ]
        };
        var b = new TableInfo
        {
            Schema = "dbo", TableName = "TableB",
            PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true }
            ]
        };
        var tables = new List<TableInfo> { a, b };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.TableB.Id|dbo.TableA.Id"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.TableB", source.Table);
    }

    [Fact]
    public void ResolveSource_CascadeNarrowsAcrossTiers()
    {
        // 3 columns: 2 are PKs (one is identity), 1 is plain.
        // Tier 2 narrows to the 2 PK columns; Tier 3 picks the identity one
        // among them. Demonstrates that the cascade carries the narrowed
        // candidate set forward instead of resetting at each tier.
        var snapshot = new TableInfo
        {
            Schema = "dbo", TableName = "Snapshot",
            PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true, IsIdentity = true }
            ]
        };
        var customer = new TableInfo
        {
            Schema = "dbo", TableName = "Customer",
            PrimaryKeyColumns = ["CustomerCode"],
            Columns =
            [
                new ColumnInfo { Name = "CustomerCode", SqlType = "nvarchar", MaxLength = 20, IsPrimaryKey = true }
            ]
        };
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders",
            Columns =
            [
                new ColumnInfo { Name = "RefCode", SqlType = "nvarchar", MaxLength = 20 }
            ]
        };
        var tables = new List<TableInfo> { snapshot, customer, orders };

        // Plain column declared first, then non-identity PK, then identity-PK.
        // Tier 2 narrows to {Customer.CustomerCode, Snapshot.Id};
        // Tier 3 narrows to {Snapshot.Id} → identity wins.
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.RefCode|dbo.Customer.CustomerCode|dbo.Snapshot.Id"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.Snapshot", source.Table);
        Assert.Equal("Id", source.Column);
    }

    [Fact]
    public void ResolveSource_ThreeColumnGroup_ExternalAtMiddleWins()
    {
        // 3-column group with the external column declared in position [1].
        // External is Tier 1, so it must win regardless of position.
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var stats = MakeTable("dbo", "Stats", "RegionCode");
        var regions = MakeTable("dbo", "Regions", "Code");
        var allTables = new List<TableInfo> { orders, stats, regions };

        // Only Orders and Stats are scoped; Regions is external.
        var scopedTables = new List<TableInfo> { orders, stats };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.RegionCode|dbo.Regions.Code|dbo.Stats.RegionCode"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.Regions", source.Table);
        Assert.True(source.IsExternalRoot);
    }

    [Fact]
    public void ResolveSource_RejectsThreeExternals()
    {
        // 3-column group where every column is external. Multi-external check
        // fires; cascade is not run.
        var orders = MakeTable("dbo", "Orders", "Code");
        var regions = MakeTable("dbo", "Regions", "Code");
        var areas = MakeTable("dbo", "Areas", "Code");
        var allTables = new List<TableInfo> { orders, regions, areas };

        var scopedTables = new List<TableInfo>(); // nothing in scope

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.Code|dbo.Regions.Code|dbo.Areas.Code"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null);

        Assert.Single(errors);
        Assert.Contains("multiple source-data providers", errors[0]);
        Assert.Contains("dbo.Orders", errors[0]);
        Assert.Contains("dbo.Regions", errors[0]);
        Assert.Contains("dbo.Areas", errors[0]);
        Assert.DoesNotContain(groups[0].Columns, c => c.IsSource);
    }

    [Fact]
    public void ResolveSource_TiedPksWithoutAutoGen_FallsToUnique()
    {
        // 3 PKs from 3 different tables; none is auto-gen; only the third
        // is also marked unique. Tier 2 narrows to all 3, Tier 3 matches 0
        // (skipped), Tier 4 picks the unique one.
        var a = new TableInfo
        {
            Schema = "dbo", TableName = "A", PrimaryKeyColumns = ["Pk"],
            Columns = [new ColumnInfo { Name = "Pk", SqlType = "int", IsPrimaryKey = true }]
        };
        var b = new TableInfo
        {
            Schema = "dbo", TableName = "B", PrimaryKeyColumns = ["Pk"],
            Columns = [new ColumnInfo { Name = "Pk", SqlType = "int", IsPrimaryKey = true }]
        };
        var c = new TableInfo
        {
            Schema = "dbo", TableName = "C", PrimaryKeyColumns = ["Pk"],
            Columns = [new ColumnInfo { Name = "Pk", SqlType = "int", IsPrimaryKey = true, IsUnique = true }]
        };
        var tables = new List<TableInfo> { a, b, c };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.A.Pk|dbo.B.Pk|dbo.C.Pk"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, tables, tables, columnScope: null);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.C", source.Table);
    }

    [Fact]
    public void ResolveSource_ExternalBeatsAllInScopeSignals()
    {
        // External should beat PK + identity + unique candidates in the same group.
        var orders = new TableInfo
        {
            Schema = "dbo", TableName = "Orders", PrimaryKeyColumns = ["Id"],
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true, IsIdentity = true, IsUnique = true }
            ]
        };
        var lookup = MakeTable("dbo", "Lookup", "Code");
        var allTables = new List<TableInfo> { orders, lookup };
        var scopedTables = new List<TableInfo> { orders };

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.Id|dbo.Lookup.Code"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(
            groups, scopedTables, allTables, columnScope: null);

        Assert.Empty(errors);
        var source = groups[0].Columns.Single(c => c.IsSource);
        Assert.Equal("dbo.Lookup", source.Table);
        Assert.True(source.IsExternalRoot);
    }

    #endregion

    #region DependencyGraph tests

    [Fact]
    public void DependencyGraph_CustomDepsAffectOrder()
    {
        var regions = MakeTable("dbo", "Regions", "Code");
        var orders = MakeTable("dbo", "Orders", "RegionCode");

        var graph = new DependencyGraph();
        graph.Build([regions, orders]);

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);
        graph.AddCustomDependencies(groups);

        var sorted = graph.GetTopologicalOrder();

        var regionsIdx = sorted.FindIndex(t => t.FullName == "dbo.Regions");
        var ordersIdx = sorted.FindIndex(t => t.FullName == "dbo.Orders");
        Assert.True(regionsIdx < ordersIdx,
            "Source table (Regions) should come before dependent table (Orders)");
    }

    [Fact]
    public void DependencyGraph_CustomDepsThreeTableChain()
    {
        var regions = MakeTable("dbo", "Regions", "Code");
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var stats = MakeTable("dbo", "RegionStats", "RegionCode");

        var graph = new DependencyGraph();
        graph.Build([regions, orders, stats]);

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode|dbo.RegionStats.RegionCode"]);
        graph.AddCustomDependencies(groups);

        var sorted = graph.GetTopologicalOrder();

        var regionsIdx = sorted.FindIndex(t => t.FullName == "dbo.Regions");
        var ordersIdx = sorted.FindIndex(t => t.FullName == "dbo.Orders");
        var statsIdx = sorted.FindIndex(t => t.FullName == "dbo.RegionStats");
        Assert.True(regionsIdx < ordersIdx);
        Assert.True(regionsIdx < statsIdx);
    }

    [Fact]
    public void DependencyGraph_CustomDepsSkipsOutOfScopeTable()
    {
        var orders = MakeTable("dbo", "Orders", "RegionCode");

        var graph = new DependencyGraph();
        graph.Build([orders]);

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);
        graph.AddCustomDependencies(groups);

        var sorted = graph.GetTopologicalOrder();
        Assert.Single(sorted);
        Assert.Equal("dbo.Orders", sorted[0].FullName);
    }

    [Fact]
    public void DependencyGraph_CustomDepsSkipsSameTable()
    {
        var orders = MakeTable("dbo", "Orders", "Col1", "Col2");

        var graph = new DependencyGraph();
        graph.Build([orders]);

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.Col1|dbo.Orders.Col2"]);
        graph.AddCustomDependencies(groups);

        var sorted = graph.GetTopologicalOrder();
        Assert.Single(sorted);
    }

    [Fact]
    public void DependencyGraph_CustomDepsCycleDetected()
    {
        var a = MakeTable("dbo", "TableA", "Col1");
        var b = MakeTable("dbo", "TableB", "Col1");

        var graph = new DependencyGraph();
        graph.Build([a, b]);

        var groups = ScopeConfig.ParseCustomDependencies(
        [
            "dbo.TableA.Col1|dbo.TableB.Col1",
            "dbo.TableB.Col1|dbo.TableA.Col1"
        ]);
        graph.AddCustomDependencies(groups);

        Assert.Throws<InvalidOperationException>(() => graph.GetTopologicalOrder());
    }

    [Fact]
    public void DependencyGraph_NullCustomDepsIsNoOp()
    {
        var orders = MakeTable("dbo", "Orders", "Col1");

        var graph = new DependencyGraph();
        graph.Build([orders]);
        graph.AddCustomDependencies(null);

        var sorted = graph.GetTopologicalOrder();
        Assert.Single(sorted);
    }

    [Fact]
    public void DependencyGraph_ExternalRootDoesNotConstrainOrder()
    {
        // Only Orders is in the graph (Regions is "external").
        // The external root must NOT introduce any edges or in-degree —
        // dependents must still be valid root nodes themselves.
        var orders = MakeTable("dbo", "Orders", "RegionCode");

        var graph = new DependencyGraph();
        graph.Build([orders]);

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);
        // Simulate validator marking the source as external.
        groups[0].Columns[0].IsExternalRoot = true;

        graph.AddCustomDependencies(groups);

        var sorted = graph.GetTopologicalOrder();
        Assert.Single(sorted);
        Assert.Equal("dbo.Orders", sorted[0].FullName);
    }

    #endregion

    #region PlanGenerator tests

    [Fact]
    public void PlanGenerator_AssignsCustomDependencyGenerator()
    {
        var regions = MakeTable("dbo", "Regions", "Code");
        var orders = MakeTable("dbo", "Orders", "RegionCode");

        var graph = new DependencyGraph();
        graph.Build([regions, orders]);
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);
        graph.AddCustomDependencies(groups);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 10, 42,
            customDependencies: groups);

        var orderPlan = plan.Tables.First(t => t.Table == "dbo.Orders");
        var regionCodeCol = orderPlan.Columns.First(c => c.Name == "RegionCode");
        Assert.Equal("customDependency", regionCodeCol.Generator);
        Assert.Equal("dbo.Regions", Helpers.GetArgString(regionCodeCol.GeneratorArgs, "sourceTable"));
        Assert.Equal("Code", Helpers.GetArgString(regionCodeCol.GeneratorArgs, "sourceColumn"));
    }

    [Fact]
    public void PlanGenerator_SourceColumnKeepsNormalGenerator()
    {
        var regions = MakeTable("dbo", "Regions", "Code");
        var orders = MakeTable("dbo", "Orders", "RegionCode");

        var graph = new DependencyGraph();
        graph.Build([regions, orders]);
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);
        graph.AddCustomDependencies(groups);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 10, 42,
            customDependencies: groups);

        var regionPlan = plan.Tables.First(t => t.Table == "dbo.Regions");
        var codeCol = regionPlan.Columns.First(c => c.Name == "Code");
        Assert.NotEqual("customDependency", codeCol.Generator);
        Assert.NotEqual("skip", codeCol.Generator);
    }

    [Fact]
    public void PlanGenerator_ThreeColumnGroup()
    {
        var regions = MakeTable("dbo", "Regions", "Code");
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var stats = MakeTable("dbo", "RegionStats", "RegionCode");

        var graph = new DependencyGraph();
        graph.Build([regions, orders, stats]);
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode|dbo.RegionStats.RegionCode"]);
        graph.AddCustomDependencies(groups);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 10, 42,
            customDependencies: groups);

        var orderCol = plan.Tables.First(t => t.Table == "dbo.Orders")
            .Columns.First(c => c.Name == "RegionCode");
        Assert.Equal("customDependency", orderCol.Generator);
        Assert.Equal("dbo.Regions", Helpers.GetArgString(orderCol.GeneratorArgs, "sourceTable"));

        var statsCol = plan.Tables.First(t => t.Table == "dbo.RegionStats")
            .Columns.First(c => c.Name == "RegionCode");
        Assert.Equal("customDependency", statsCol.Generator);
        Assert.Equal("dbo.Regions", Helpers.GetArgString(statsCol.GeneratorArgs, "sourceTable"));
    }

    [Fact]
    public void PlanGenerator_FkTakesPrecedenceOverCustomDep()
    {
        var table = new TableInfo
        {
            Schema = "dbo",
            TableName = "Orders",
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true },
                new ColumnInfo { Name = "CustomerId", SqlType = "int" }
            ],
            PrimaryKeyColumns = ["Id"],
            ForeignKeys =
            [
                new ForeignKeyInfo
                {
                    FkName = "FK_Orders_Customers",
                    ParentSchema = "dbo", ParentTable = "Orders", ParentColumn = "CustomerId",
                    ReferencedSchema = "dbo", ReferencedTable = "Customers", ReferencedColumn = "Id"
                }
            ]
        };
        var customers = MakeTable("dbo", "Customers");

        var graph = new DependencyGraph();
        graph.Build([customers, table]);
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Customers.Id|dbo.Orders.CustomerId"]);
        graph.AddCustomDependencies(groups);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 10, 42,
            customDependencies: groups);

        var fkCol = plan.Tables.First(t => t.Table == "dbo.Orders")
            .Columns.First(c => c.Name == "CustomerId");
        Assert.Equal("foreignKey", fkCol.Generator);
    }

    [Fact]
    public void PlanGenerator_CustomDepsStoredOnPlan()
    {
        var a = MakeTable("dbo", "TableA", "Col1");
        var b = MakeTable("dbo", "TableB", "Col1");
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.TableA.Col1|dbo.TableB.Col1"]);

        var graph = new DependencyGraph();
        graph.Build([a, b]);
        graph.AddCustomDependencies(groups);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 10, 42,
            customDependencies: groups);

        Assert.NotNull(plan.CustomDependencies);
        Assert.Single(plan.CustomDependencies);
        Assert.Equal(2, plan.CustomDependencies[0].Columns.Count);
    }

    [Fact]
    public void PlanGenerator_NullCustomDepsOmittedFromPlan()
    {
        var a = MakeTable("dbo", "TableA", "Col1");

        var planGen = new PlanGenerator();
        var plan = planGen.Generate([a], new HashSet<string>(), 10, 42);

        Assert.Null(plan.CustomDependencies);
    }

    [Fact]
    public void PlanGenerator_ExternalRootEmitsIsExternalArg()
    {
        // Only Orders is in scope; Regions.Code is the external root source.
        var orders = MakeTable("dbo", "Orders", "RegionCode");

        var graph = new DependencyGraph();
        graph.Build([orders]);

        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);
        groups[0].Columns[0].IsExternalRoot = true;
        graph.AddCustomDependencies(groups);

        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 10, 42,
            customDependencies: groups);

        var col = plan.Tables.First(t => t.Table == "dbo.Orders")
            .Columns.First(c => c.Name == "RegionCode");
        Assert.Equal("customDependency", col.Generator);
        Assert.Equal("dbo.Regions", Helpers.GetArgString(col.GeneratorArgs, "sourceTable"));
        Assert.Equal("Code", Helpers.GetArgString(col.GeneratorArgs, "sourceColumn"));
        Assert.True(col.GeneratorArgs.TryGetValue("isExternal", out var ext)
                    && Helpers.IsTruthy(ext));
    }

    #endregion

    #region Runtime linking tests

    [Fact]
    public void ColumnValueGenerator_CustomDependency_ReturnsNull()
    {
        var gen = new ColumnValueGenerator(seed: 42);
        var plan = new ColumnPlan
        {
            Name = "RegionCode",
            SqlType = "int",
            Generator = "customDependency",
            GeneratorArgs = new Dictionary<string, object?>
            {
                ["sourceTable"] = "dbo.Regions",
                ["sourceColumn"] = "Code"
            }
        };

        var result = gen.GenerateFromPlan(plan);

        Assert.Null(result);
    }

    [Fact]
    public void BuildCustomDepGroupsFromPlan_ExtractsGroups()
    {
        var columns = new List<ColumnPlan>
        {
            new()
            {
                Name = "Id", SqlType = "int", IsPrimaryKey = true,
                Generator = "Random.Int"
            },
            new()
            {
                Name = "RegionCode", SqlType = "int",
                Generator = "customDependency",
                GeneratorArgs = new Dictionary<string, object?>
                {
                    ["sourceTable"] = "dbo.Regions",
                    ["sourceColumn"] = "Code"
                }
            },
            new()
            {
                Name = "Name", SqlType = "nvarchar", MaxLength = 100,
                Generator = "Lorem.Word"
            }
        };

        var groups = DataInserter.BuildCustomDepGroupsFromPlan(columns);

        Assert.Single(groups);
        Assert.Equal("dbo.Regions", groups[0].SourceTable);
        Assert.Equal("Code", groups[0].SourceColumn);
        Assert.Equal("RegionCode", groups[0].DependentColumn);
    }

    [Fact]
    public void BuildCustomDepGroupsFromPlan_EmptyWhenNoCustomDeps()
    {
        var columns = new List<ColumnPlan>
        {
            new() { Name = "Id", SqlType = "int", Generator = "Random.Int" },
            new() { Name = "Name", SqlType = "nvarchar", MaxLength = 100, Generator = "Lorem.Word" }
        };

        var groups = DataInserter.BuildCustomDepGroupsFromPlan(columns);

        Assert.Empty(groups);
    }

    [Fact]
    public void BuildCustomDepGroupsFromPlan_MultipleCustomDeps()
    {
        var columns = new List<ColumnPlan>
        {
            new()
            {
                Name = "RegionCode", SqlType = "int",
                Generator = "customDependency",
                GeneratorArgs = new Dictionary<string, object?>
                {
                    ["sourceTable"] = "dbo.Regions",
                    ["sourceColumn"] = "Code"
                }
            },
            new()
            {
                Name = "CategoryId", SqlType = "int",
                Generator = "customDependency",
                GeneratorArgs = new Dictionary<string, object?>
                {
                    ["sourceTable"] = "dbo.Categories",
                    ["sourceColumn"] = "Id"
                }
            }
        };

        var groups = DataInserter.BuildCustomDepGroupsFromPlan(columns);

        Assert.Equal(2, groups.Count);
        Assert.Equal("dbo.Regions", groups[0].SourceTable);
        Assert.Equal("dbo.Categories", groups[1].SourceTable);
    }

    [Fact]
    public void BuildCustomDepGroupsFromPlan_PreservesIsExternal()
    {
        var columns = new List<ColumnPlan>
        {
            new()
            {
                Name = "RegionCode", SqlType = "int",
                Generator = "customDependency",
                GeneratorArgs = new Dictionary<string, object?>
                {
                    ["sourceTable"] = "dbo.Regions",
                    ["sourceColumn"] = "Code",
                    ["isExternal"] = true
                }
            },
            new()
            {
                Name = "CategoryId", SqlType = "int",
                Generator = "customDependency",
                GeneratorArgs = new Dictionary<string, object?>
                {
                    ["sourceTable"] = "dbo.Categories",
                    ["sourceColumn"] = "Id",
                    ["isExternal"] = false
                }
            }
        };

        var groups = DataInserter.BuildCustomDepGroupsFromPlan(columns);

        Assert.Equal(2, groups.Count);
        Assert.True(groups[0].IsExternal);
        Assert.False(groups[1].IsExternal);
    }

    [Fact]
    public void BuildCustomDepGroupsFromPlan_PreservesNullability()
    {
        var columns = new List<ColumnPlan>
        {
            new()
            {
                Name = "RegionCode", SqlType = "int", IsNullable = true,
                Generator = "customDependency",
                GeneratorArgs = new Dictionary<string, object?>
                {
                    ["sourceTable"] = "dbo.Regions",
                    ["sourceColumn"] = "Code"
                }
            }
        };

        var groups = DataInserter.BuildCustomDepGroupsFromPlan(columns);

        Assert.Single(groups);
        Assert.True(groups[0].IsNullable);
    }

    [Fact]
    public void ResolveCustomDepValues_CopiesValueFromSourceTable()
    {
        var valueGen = new ColumnValueGenerator(seed: 42);
        var inserter = new DataInserter("unused", valueGen, new HashSet<string>());

        inserter._generatedKeys["dbo.Regions"] =
        [
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Code"] = 10 },
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Code"] = 20 },
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Code"] = 30 },
        ];

        var groups = new List<DataInserter.CustomDepGroup>
        {
            new("dbo.Regions", "Code", "RegionCode", false)
        };

        var resolved = inserter.ResolveCustomDepValues(groups);

        Assert.Single(resolved);
        Assert.True(resolved.ContainsKey("RegionCode"));
        var value = (int)resolved["RegionCode"]!;
        Assert.Contains(value, new[] { 10, 20, 30 });
    }

    [Fact]
    public void ResolveCustomDepValues_ReturnsEmptyWhenNoSourceData()
    {
        var valueGen = new ColumnValueGenerator(seed: 42);
        var inserter = new DataInserter("unused", valueGen, new HashSet<string>());

        var groups = new List<DataInserter.CustomDepGroup>
        {
            new("dbo.Regions", "Code", "RegionCode", false)
        };

        var resolved = inserter.ResolveCustomDepValues(groups);

        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolveCustomDepValues_ReturnsEmptyWhenNullGroups()
    {
        var valueGen = new ColumnValueGenerator(seed: 42);
        var inserter = new DataInserter("unused", valueGen, new HashSet<string>());

        var resolved = inserter.ResolveCustomDepValues(null);

        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolveCustomDepValues_MultipleDepsSameSource()
    {
        var valueGen = new ColumnValueGenerator(seed: 42);
        var inserter = new DataInserter("unused", valueGen, new HashSet<string>());

        inserter._generatedKeys["dbo.Lookup"] =
        [
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["Id"] = 5,
                ["Name"] = "Test"
            },
        ];

        var groups = new List<DataInserter.CustomDepGroup>
        {
            new("dbo.Lookup", "Id", "LookupId", false),
            new("dbo.Lookup", "Name", "LookupName", false),
        };

        var resolved = inserter.ResolveCustomDepValues(groups);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(5, resolved["LookupId"]);
        Assert.Equal("Test", resolved["LookupName"]);
    }

    [Fact]
    public void ResolveCustomDepValues_AllRowsPickedOverManyIterations()
    {
        var valueGen = new ColumnValueGenerator(seed: 42);
        var inserter = new DataInserter("unused", valueGen, new HashSet<string>());

        inserter._generatedKeys["dbo.Regions"] =
        [
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Code"] = 1 },
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Code"] = 2 },
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Code"] = 3 },
        ];

        var groups = new List<DataInserter.CustomDepGroup>
        {
            new("dbo.Regions", "Code", "RegionCode", false)
        };

        var seenValues = new HashSet<int>();
        for (var i = 0; i < 200; i++)
        {
            var resolved = inserter.ResolveCustomDepValues(groups);
            seenValues.Add((int)resolved["RegionCode"]!);
        }

        Assert.Contains(1, seenValues);
        Assert.Contains(2, seenValues);
        Assert.Contains(3, seenValues);
    }

    [Fact]
    public void ResolveCustomDepValues_SkipsColumnNotInSourceRow()
    {
        var valueGen = new ColumnValueGenerator(seed: 42);
        var inserter = new DataInserter("unused", valueGen, new HashSet<string>());

        inserter._generatedKeys["dbo.Regions"] =
        [
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Id"] = 1 },
        ];

        var groups = new List<DataInserter.CustomDepGroup>
        {
            new("dbo.Regions", "NonExistentColumn", "RegionCode", false)
        };

        var resolved = inserter.ResolveCustomDepValues(groups);

        Assert.Empty(resolved);
    }

    #endregion

    #region YAML round-trip tests

    [Fact]
    public void CustomDependencies_YamlRoundTrip()
    {
        var plan = new GenerationPlan
        {
            Mode = "insert",
            Seed = 42,
            Locale = "en",
            Tables = [],
            CustomDependencies =
            [
                new CustomDependencyGroup
                {
                    Columns =
                    [
                        new CustomColumnRef { Table = "dbo.Regions", Column = "Code" },
                        new CustomColumnRef { Table = "dbo.Orders", Column = "RegionCode" },
                        new CustomColumnRef { Table = "dbo.RegionStats", Column = "RegionCode" }
                    ]
                }
            ]
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();
        var yaml = serializer.Serialize(plan);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var restored = deserializer.Deserialize<GenerationPlan>(yaml);

        Assert.NotNull(restored.CustomDependencies);
        Assert.Single(restored.CustomDependencies);
        var group = restored.CustomDependencies[0];
        Assert.Equal(3, group.Columns.Count);
        Assert.Equal("dbo.Regions", group.Columns[0].Table);
        Assert.Equal("Code", group.Columns[0].Column);
        Assert.Equal("dbo.Orders", group.Columns[1].Table);
        Assert.Equal("RegionCode", group.Columns[1].Column);
        Assert.Equal("dbo.RegionStats", group.Columns[2].Table);
        Assert.Equal("RegionCode", group.Columns[2].Column);
    }

    [Fact]
    public void CustomDependencies_NullOmittedInYaml()
    {
        var plan = new GenerationPlan
        {
            Mode = "insert",
            Tables = [],
            CustomDependencies = null
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();
        var yaml = serializer.Serialize(plan);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var restored = deserializer.Deserialize<GenerationPlan>(yaml);

        Assert.Null(restored.CustomDependencies);
    }

    [Fact]
    public void CustomDependencyGenerator_SurvivesYamlRoundTrip()
    {
        var regions = MakeTable("dbo", "Regions", "Code");
        var orders = MakeTable("dbo", "Orders", "RegionCode");

        var graph = new DependencyGraph();
        graph.Build([regions, orders]);
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Regions.Code|dbo.Orders.RegionCode"]);
        graph.AddCustomDependencies(groups);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 5, 42,
            customDependencies: groups);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();
        var yaml = serializer.Serialize(plan);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var restored = deserializer.Deserialize<GenerationPlan>(yaml);

        var orderPlan = restored.Tables.First(t => t.Table == "dbo.Orders");
        var regionCodeCol = orderPlan.Columns.First(c => c.Name == "RegionCode");
        Assert.Equal("customDependency", regionCodeCol.Generator);
        Assert.Equal("dbo.Regions", Helpers.GetArgString(regionCodeCol.GeneratorArgs, "sourceTable"));
        Assert.Equal("Code", Helpers.GetArgString(regionCodeCol.GeneratorArgs, "sourceColumn"));

        Assert.NotNull(restored.CustomDependencies);
        Assert.Single(restored.CustomDependencies);
    }

    [Fact]
    public void IsSourceFlag_SurvivesYamlRoundTrip()
    {
        // The validator stamps IsSource on whichever column the cascade picks.
        // That flag must survive serialization so a re-loaded plan retains the
        // resolved source instead of falling back to "first declared".
        var plan = new GenerationPlan
        {
            Mode = "insert",
            Locale = "en",
            Tables = [],
            CustomDependencies =
            [
                new CustomDependencyGroup
                {
                    Columns =
                    [
                        new CustomColumnRef { Table = "dbo.Orders", Column = "RegionCode" },
                        // Source is the second column.
                        new CustomColumnRef { Table = "dbo.Regions", Column = "Code", IsSource = true }
                    ]
                }
            ]
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
            .Build();
        var yaml = serializer.Serialize(plan);

        Assert.Contains("isSource: true", yaml);
        Assert.DoesNotContain("isSource: false", yaml);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var restored = deserializer.Deserialize<GenerationPlan>(yaml);

        Assert.NotNull(restored.CustomDependencies);
        var group = restored.CustomDependencies[0];
        Assert.False(group.Columns[0].IsSource);
        Assert.True(group.Columns[1].IsSource);
        Assert.Equal("dbo.Regions", group.Columns[1].Table);
    }

    [Fact]
    public void PlanGenerator_NonFirstSource_DependentGetsCustomDependency()
    {
        // When IsSource is set on a column other than [0], BuildCustomDependencyLookup
        // must still wire the dependents to the chosen source — not to Columns[0].
        var orders = MakeTable("dbo", "Orders", "RegionCode");
        var regions = MakeTable("dbo", "Regions", "Code");

        var graph = new DependencyGraph();
        graph.Build([orders, regions]);

        // Declare dependent first; mark source as the second column. This is what
        // the validator's cascade would produce if the user wrote them in this order
        // and Regions.Code was a PK / Unique.
        var groups = new List<CustomDependencyGroup>
        {
            new()
            {
                Columns =
                [
                    new CustomColumnRef { Table = "dbo.Orders", Column = "RegionCode" },
                    new CustomColumnRef { Table = "dbo.Regions", Column = "Code", IsSource = true }
                ]
            }
        };
        graph.AddCustomDependencies(groups);
        var sorted = graph.GetTopologicalOrder();

        // Source table (Regions) must come before dependent (Orders).
        var regionsIdx = sorted.FindIndex(t => t.FullName == "dbo.Regions");
        var ordersIdx = sorted.FindIndex(t => t.FullName == "dbo.Orders");
        Assert.True(regionsIdx < ordersIdx);

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 5, 42,
            customDependencies: groups);

        // Orders.RegionCode is the dependent → customDependency wired to Regions.Code.
        var regionCodeCol = plan.Tables.First(t => t.Table == "dbo.Orders")
            .Columns.First(c => c.Name == "RegionCode");
        Assert.Equal("customDependency", regionCodeCol.Generator);
        Assert.Equal("dbo.Regions", Helpers.GetArgString(regionCodeCol.GeneratorArgs, "sourceTable"));
        Assert.Equal("Code", Helpers.GetArgString(regionCodeCol.GeneratorArgs, "sourceColumn"));

        // Regions.Code stays a regular generator (it's the source, not a dependent).
        var sourceCol = plan.Tables.First(t => t.Table == "dbo.Regions")
            .Columns.First(c => c.Name == "Code");
        Assert.NotEqual("customDependency", sourceCol.Generator);
    }

    #endregion
}
