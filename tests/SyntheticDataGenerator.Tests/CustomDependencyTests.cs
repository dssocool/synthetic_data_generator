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
        var raw = new[] { "dbo.Orders.RegionCode,dbo.Regions.Code,dbo.RegionStats.RegionCode" };
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
            "dbo.Orders.RegionCode,dbo.Regions.Code",
            "dbo.Products.CategoryName,dbo.Categories.Name"
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
        var raw = new[] { "", "  ", "dbo.A.Col1,dbo.B.Col2" };
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
    public void ParseCustomDependencies_HandlesSpacesAroundCommas()
    {
        var raw = new[] { "dbo.Orders.Col1 , dbo.Regions.Col2 , dbo.Stats.Col3" };
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
            ["dbo.Orders.RegionCode,dbo.Regions.Code"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(groups, tables);

        Assert.Empty(errors);
    }

    [Fact]
    public void CollectCustomDependencyErrors_MissingTable()
    {
        var tables = new List<TableInfo>
        {
            MakeTable("dbo", "Orders", "RegionCode")
        };
        var groups = ScopeConfig.ParseCustomDependencies(
            ["dbo.Orders.RegionCode,dbo.Regions.Code"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(groups, tables);

        Assert.Single(errors);
        Assert.Contains("dbo.Regions", errors[0]);
        Assert.Contains("not in scope", errors[0]);
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
            ["dbo.Orders.RegionCode,dbo.Regions.Code"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(groups, tables);

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
            ["dbo.Orders.RegionCode,dbo.Regions.Code"]);

        var errors = DataGenerationPlanner.CollectCustomDependencyErrors(groups, tables);

        Assert.Equal(2, errors.Count);
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
            ["dbo.Regions.Code,dbo.Orders.RegionCode"]);
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
            ["dbo.Regions.Code,dbo.Orders.RegionCode,dbo.RegionStats.RegionCode"]);
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
            ["dbo.Regions.Code,dbo.Orders.RegionCode"]);
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
            ["dbo.Orders.Col1,dbo.Orders.Col2"]);
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
            "dbo.TableA.Col1,dbo.TableB.Col1",
            "dbo.TableB.Col1,dbo.TableA.Col1"
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
            ["dbo.Regions.Code,dbo.Orders.RegionCode"]);
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
            ["dbo.Regions.Code,dbo.Orders.RegionCode"]);
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
            ["dbo.Regions.Code,dbo.Orders.RegionCode,dbo.RegionStats.RegionCode"]);
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
            ["dbo.Customers.Id,dbo.Orders.CustomerId"]);
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
            ["dbo.TableA.Col1,dbo.TableB.Col1"]);

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

    #endregion

    #region YAML round-trip tests

    [Fact]
    public void CustomDependencies_YamlRoundTrip()
    {
        var plan = new GenerationPlan
        {
            Mode = "bootstrap",
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
            Mode = "bootstrap",
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
            ["dbo.Regions.Code,dbo.Orders.RegionCode"]);
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

    #endregion
}
