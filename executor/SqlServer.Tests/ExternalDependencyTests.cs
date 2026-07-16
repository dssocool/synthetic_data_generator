using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SyntheticDataGenerator.Tests;

public class ExternalDependencyTests
{
    private static TableInfo MakeTable(string schema, string name, params string[] fks)
    {
        var table = new TableInfo
        {
            Schema = schema,
            TableName = name,
            Columns = [new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true }],
            PrimaryKeyColumns = ["Id"]
        };

        for (var i = 0; i < fks.Length; i += 3)
        {
            var refFullName = fks[i];
            var parentCol = fks[i + 1];
            var refCol = fks[i + 2];
            var dotIdx = refFullName.IndexOf('.');
            table.Columns.Add(new ColumnInfo { Name = parentCol, SqlType = "int" });
            table.ForeignKeys.Add(new ForeignKeyInfo
            {
                FkName = $"FK_{name}_{parentCol}",
                ParentSchema = schema,
                ParentTable = name,
                ParentColumn = parentCol,
                ReferencedSchema = dotIdx >= 0 ? refFullName[..dotIdx] : schema,
                ReferencedTable = dotIdx >= 0 ? refFullName[(dotIdx + 1)..] : refFullName,
                ReferencedColumn = refCol
            });
        }

        return table;
    }

    [Fact]
    public void CollectExternalDependencies_DetectsOutbound()
    {
        var orders = MakeTable("dbo", "Orders", "dbo.Customers", "CustomerId", "Id");
        var scopedTables = new List<TableInfo> { orders };

        var customers = MakeTable("dbo", "Customers");
        var allTables = new List<TableInfo> { orders, customers };

        var deps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);

        Assert.Single(deps);
        var dep = deps[0];
        Assert.Equal("outbound", dep.Direction);
        Assert.Equal("dbo.Orders", dep.ScopedTable);
        Assert.Equal("CustomerId", dep.ScopedColumn);
        Assert.Equal("dbo.Customers", dep.ExternalTable);
        Assert.Equal("Id", dep.ExternalColumn);
    }

    [Fact]
    public void CollectExternalDependencies_DetectsInbound()
    {
        var customers = MakeTable("dbo", "Customers");
        var scopedTables = new List<TableInfo> { customers };

        var orders = MakeTable("dbo", "Orders", "dbo.Customers", "CustomerId", "Id");
        var allTables = new List<TableInfo> { customers, orders };

        var deps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);

        Assert.Single(deps);
        var dep = deps[0];
        Assert.Equal("inbound", dep.Direction);
        Assert.Equal("dbo.Customers", dep.ScopedTable);
        Assert.Equal("Id", dep.ScopedColumn);
        Assert.Equal("dbo.Orders", dep.ExternalTable);
        Assert.Equal("CustomerId", dep.ExternalColumn);
    }

    [Fact]
    public void CollectExternalDependencies_IgnoresInScopeFks()
    {
        var customers = MakeTable("dbo", "Customers");
        var orders = MakeTable("dbo", "Orders", "dbo.Customers", "CustomerId", "Id");
        var scopedTables = new List<TableInfo> { customers, orders };
        var allTables = new List<TableInfo> { customers, orders };

        var deps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);

        Assert.Empty(deps);
    }

    [Fact]
    public void CollectExternalDependencies_IgnoresSelfReferencing()
    {
        var categories = MakeTable("dbo", "Categories", "dbo.Categories", "ParentId", "Id");
        var scopedTables = new List<TableInfo> { categories };
        var allTables = new List<TableInfo> { categories };

        var deps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);

        Assert.Empty(deps);
    }

    [Fact]
    public void CollectExternalDependencies_BothDirections()
    {
        var orders = MakeTable("dbo", "Orders", "dbo.Customers", "CustomerId", "Id");
        var scopedTables = new List<TableInfo> { orders };

        var customers = MakeTable("dbo", "Customers");
        var orderItems = MakeTable("dbo", "OrderItems", "dbo.Orders", "OrderId", "Id");
        var allTables = new List<TableInfo> { orders, customers, orderItems };

        var deps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);

        Assert.Equal(2, deps.Count);

        var outbound = deps.First(d => d.Direction == "outbound");
        Assert.Equal("dbo.Orders", outbound.ScopedTable);
        Assert.Equal("dbo.Customers", outbound.ExternalTable);

        var inbound = deps.First(d => d.Direction == "inbound");
        Assert.Equal("dbo.Orders", inbound.ScopedTable);
        Assert.Equal("dbo.OrderItems", inbound.ExternalTable);
    }

    [Fact]
    public void CollectExternalDependencies_EmptyWhenNoScope()
    {
        var deps = DataGenerationPlanner.CollectExternalDependencies([], []);
        Assert.Empty(deps);
    }

    [Fact]
    public void PlanGenerator_SetsIsExternalOnOutboundFk()
    {
        var orders = MakeTable("dbo", "Orders", "dbo.Customers", "CustomerId", "Id");
        var scopedTables = new List<TableInfo> { orders };
        var allTables = new List<TableInfo> { orders, MakeTable("dbo", "Customers") };

        var externalDeps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);
        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            scopedTables, new HashSet<string>(), 10, 42, "en", "insert",
            columnsInScope: null, externalDependencies: externalDeps);

        var fkCol = plan.Tables[0].Columns.First(c =>
            c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase));
        Assert.True(fkCol.GeneratorArgs.TryGetValue("isExternal", out var isExt));
        Assert.True(Helpers.IsTruthy(isExt));
    }

    [Fact]
    public void PlanGenerator_DoesNotSetIsExternalForInScopeFk()
    {
        var customers = MakeTable("dbo", "Customers");
        var orders = MakeTable("dbo", "Orders", "dbo.Customers", "CustomerId", "Id");
        var scopedTables = new List<TableInfo> { customers, orders };

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            scopedTables, new HashSet<string>(), 10, 42, "en", "insert",
            columnsInScope: null, externalDependencies: null);

        var fkCol = plan.Tables[1].Columns.First(c =>
            c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase));
        Assert.True(fkCol.GeneratorArgs.TryGetValue("isExternal", out var isExt));
        Assert.False(Helpers.IsTruthy(isExt));
    }

    [Fact]
    public void PlanGenerator_MultipleOutboundFksFromOneTable()
    {
        var orders = MakeTable("dbo", "Orders",
            "dbo.Customers", "CustomerId", "Id",
            "dbo.Shippers", "ShipperId", "Id");
        var scopedTables = new List<TableInfo> { orders };
        var allTables = new List<TableInfo>
        {
            orders, MakeTable("dbo", "Customers"), MakeTable("dbo", "Shippers")
        };

        var externalDeps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);
        Assert.Equal(2, externalDeps.Count(d => d.Direction == "outbound"));

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            scopedTables, new HashSet<string>(), 10, 42, "en", "insert",
            columnsInScope: null, externalDependencies: externalDeps);

        var fkCols = plan.Tables[0].Columns
            .Where(c => c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, fkCols.Count);
        Assert.All(fkCols, c => Assert.True(Helpers.IsTruthy(c.GeneratorArgs["isExternal"])));

        var customerFk = fkCols.First(c => c.Name == "CustomerId");
        Assert.Equal("dbo.Customers", Helpers.GetArgString(customerFk.GeneratorArgs, "referencedTable"));
        Assert.Equal("Id", Helpers.GetArgString(customerFk.GeneratorArgs, "referencedColumn"));

        var shipperFk = fkCols.First(c => c.Name == "ShipperId");
        Assert.Equal("dbo.Shippers", Helpers.GetArgString(shipperFk.GeneratorArgs, "referencedTable"));
        Assert.Equal("Id", Helpers.GetArgString(shipperFk.GeneratorArgs, "referencedColumn"));
    }

    [Fact]
    public void PlanGenerator_MixedInScopeAndOutboundFks()
    {
        var customers = MakeTable("dbo", "Customers");
        var orders = MakeTable("dbo", "Orders",
            "dbo.Customers", "CustomerId", "Id",
            "dbo.Regions", "RegionId", "Id");
        var scopedTables = new List<TableInfo> { customers, orders };
        var allTables = new List<TableInfo>
        {
            customers, orders, MakeTable("dbo", "Regions")
        };

        var externalDeps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);
        Assert.Single(externalDeps);
        Assert.Equal("outbound", externalDeps[0].Direction);
        Assert.Equal("RegionId", externalDeps[0].ScopedColumn);

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            scopedTables, new HashSet<string>(), 10, 42, "en", "insert",
            columnsInScope: null, externalDependencies: externalDeps);

        var orderPlan = plan.Tables.First(t => t.Table == "dbo.Orders");
        var customerFk = orderPlan.Columns.First(c => c.Name == "CustomerId");
        Assert.Equal("foreignKey", customerFk.Generator);
        Assert.False(Helpers.IsTruthy(customerFk.GeneratorArgs["isExternal"]));

        var regionFk = orderPlan.Columns.First(c => c.Name == "RegionId");
        Assert.Equal("foreignKey", regionFk.Generator);
        Assert.True(Helpers.IsTruthy(regionFk.GeneratorArgs["isExternal"]));
    }

    [Fact]
    public void PlanGenerator_OutboundFkNullableColumn()
    {
        var table = new TableInfo
        {
            Schema = "dbo",
            TableName = "Orders",
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", IsPrimaryKey = true },
                new ColumnInfo { Name = "CustomerId", SqlType = "int", IsNullable = true }
            ],
            PrimaryKeyColumns = ["Id"],
            ForeignKeys =
            [
                new ForeignKeyInfo
                {
                    FkName = "FK_Orders_CustomerId",
                    ParentSchema = "dbo", ParentTable = "Orders", ParentColumn = "CustomerId",
                    ReferencedSchema = "dbo", ReferencedTable = "Customers", ReferencedColumn = "Id"
                }
            ]
        };

        var scopedTables = new List<TableInfo> { table };
        var allTables = new List<TableInfo> { table, MakeTable("dbo", "Customers") };

        var externalDeps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);
        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            scopedTables, new HashSet<string>(), 10, 42, "en", "insert",
            columnsInScope: null, externalDependencies: externalDeps);

        var fkCol = plan.Tables[0].Columns.First(c => c.Name == "CustomerId");
        Assert.Equal("foreignKey", fkCol.Generator);
        Assert.True(fkCol.IsNullable);
        Assert.True(Helpers.IsTruthy(fkCol.GeneratorArgs["isExternal"]));
        Assert.Equal("dbo.Customers", Helpers.GetArgString(fkCol.GeneratorArgs, "referencedTable"));
    }

    [Fact]
    public void PlanGenerator_OutboundFkSurvivesYamlRoundTrip()
    {
        var orders = MakeTable("dbo", "Orders", "dbo.Customers", "CustomerId", "Id");
        var scopedTables = new List<TableInfo> { orders };
        var allTables = new List<TableInfo> { orders, MakeTable("dbo", "Customers") };

        var externalDeps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);
        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            scopedTables, new HashSet<string>(), 5, 42, "en", "insert",
            columnsInScope: null, externalDependencies: externalDeps);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();
        var yaml = serializer.Serialize(plan);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var restored = deserializer.Deserialize<GenerationPlan>(yaml);

        var fkCol = restored.Tables[0].Columns.First(c =>
            c.Generator.Equals("foreignKey", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("CustomerId", fkCol.Name);
        Assert.Equal("dbo.Customers", Helpers.GetArgString(fkCol.GeneratorArgs, "referencedTable"));
        Assert.Equal("Id", Helpers.GetArgString(fkCol.GeneratorArgs, "referencedColumn"));
        Assert.True(Helpers.IsTruthy(fkCol.GeneratorArgs["isExternal"]));

        Assert.NotNull(restored.ExternalDependencies);
        var outbound = restored.ExternalDependencies.First(d => d.Direction == "outbound");
        Assert.Equal("dbo.Orders", outbound.ScopedTable);
        Assert.Equal("CustomerId", outbound.ScopedColumn);
        Assert.Equal("dbo.Customers", outbound.ExternalTable);
    }

    [Fact]
    public void PlanGenerator_ExternalDependenciesListOnPlan()
    {
        var orders = MakeTable("dbo", "Orders", "dbo.Customers", "CustomerId", "Id");
        var scopedTables = new List<TableInfo> { orders };

        var customers = MakeTable("dbo", "Customers");
        var orderItems = MakeTable("dbo", "OrderItems", "dbo.Orders", "OrderId", "Id");
        var allTables = new List<TableInfo> { orders, customers, orderItems };

        var externalDeps = DataGenerationPlanner.CollectExternalDependencies(scopedTables, allTables);
        var planGen = new PlanGenerator();
        var plan = planGen.Generate(
            scopedTables, new HashSet<string>(), 10, 42, "en", "insert",
            columnsInScope: null, externalDependencies: externalDeps);

        Assert.NotNull(plan.ExternalDependencies);
        Assert.Equal(2, plan.ExternalDependencies.Count);

        var outbound = plan.ExternalDependencies.First(d => d.Direction == "outbound");
        Assert.Equal("dbo.Orders", outbound.ScopedTable);
        Assert.Equal("CustomerId", outbound.ScopedColumn);
        Assert.Equal("dbo.Customers", outbound.ExternalTable);

        var inbound = plan.ExternalDependencies.First(d => d.Direction == "inbound");
        Assert.Equal("dbo.Orders", inbound.ScopedTable);
        Assert.Equal("Id", inbound.ScopedColumn);
        Assert.Equal("dbo.OrderItems", inbound.ExternalTable);
    }

    [Fact]
    public void ExternalDependencies_YamlRoundTrip()
    {
        var plan = new GenerationPlan
        {
            Mode = "insert",
            Seed = 42,
            Locale = "en",
            Tables = [],
            ExternalDependencies =
            [
                new ExternalDependency
                {
                    FkName = "FK_Orders_Customers",
                    Direction = "outbound",
                    ScopedTable = "dbo.Orders",
                    ScopedColumn = "CustomerId",
                    ExternalTable = "dbo.Customers",
                    ExternalColumn = "Id"
                },
                new ExternalDependency
                {
                    FkName = "FK_OrderItems_Orders",
                    Direction = "inbound",
                    ScopedTable = "dbo.Orders",
                    ScopedColumn = "Id",
                    ExternalTable = "dbo.OrderItems",
                    ExternalColumn = "OrderId"
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

        Assert.NotNull(restored.ExternalDependencies);
        Assert.Equal(2, restored.ExternalDependencies.Count);

        var outbound = restored.ExternalDependencies.First(d => d.Direction == "outbound");
        Assert.Equal("FK_Orders_Customers", outbound.FkName);
        Assert.Equal("dbo.Orders", outbound.ScopedTable);
        Assert.Equal("CustomerId", outbound.ScopedColumn);
        Assert.Equal("dbo.Customers", outbound.ExternalTable);
        Assert.Equal("Id", outbound.ExternalColumn);

        var inbound = restored.ExternalDependencies.First(d => d.Direction == "inbound");
        Assert.Equal("FK_OrderItems_Orders", inbound.FkName);
        Assert.Equal("dbo.Orders", inbound.ScopedTable);
        Assert.Equal("Id", inbound.ScopedColumn);
        Assert.Equal("dbo.OrderItems", inbound.ExternalTable);
        Assert.Equal("OrderId", inbound.ExternalColumn);
    }

    [Fact]
    public void ExternalDependencies_NullOmittedInYaml()
    {
        var plan = new GenerationPlan
        {
            Mode = "insert",
            Tables = [],
            ExternalDependencies = null
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

        Assert.Null(restored.ExternalDependencies);
    }
}
