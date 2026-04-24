using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;

namespace SyntheticDataGenerator.Tests;

public class NarrowColumnCardinalityTests
{
    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static ColumnPlan MakeColumnPlan(
        string name, string sqlType, int maxLength,
        bool isPrimaryKey = false, bool isUnique = false,
        string generator = "Random.AlphaNumeric") =>
        new()
        {
            Name = name,
            SqlType = sqlType,
            MaxLength = maxLength,
            IsPrimaryKey = isPrimaryKey,
            IsUnique = isUnique,
            Generator = generator
        };

    private static TablePlan MakeTablePlan(
        string table, int rowCount, List<ColumnPlan> columns,
        List<UniqueConstraintPlan>? uniqueConstraints = null) =>
        new()
        {
            Table = table,
            RowCount = rowCount,
            Columns = columns,
            UniqueConstraints = uniqueConstraints
        };

    private static TableInfo MakeTableInfo(string schema, string tableName) =>
        new() { Schema = schema, TableName = tableName };

    // ──────────────────────────────────────────────
    // MaxCardinalityForColumn
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("char", 1, 36)]        // 36^1
    [InlineData("char", 2, 1296)]      // 36^2
    [InlineData("char", 3, 46656)]     // 36^3
    [InlineData("nchar", 2, 36)]       // nchar(2) = 2 bytes = 1 effective char → 36^1
    [InlineData("nchar", 4, 1296)]     // nchar(4) = 4 bytes = 2 effective chars → 36^2
    public void MaxCardinalityForColumn_CharTypes_ReturnsExpected(
        string sqlType, int maxLength, long expected)
    {
        var col = MakeColumnPlan("Code", sqlType, maxLength, isPrimaryKey: true);
        Assert.Equal(expected, PlanGenerator.MaxCardinalityForColumn(col));
    }

    [Fact]
    public void MaxCardinalityForColumn_Bit_Returns2()
    {
        var col = MakeColumnPlan("IsActive", "bit", 1, isPrimaryKey: true, generator: "Random.Bool");
        Assert.Equal(2, PlanGenerator.MaxCardinalityForColumn(col));
    }

    [Fact]
    public void MaxCardinalityForColumn_TinyInt_Returns256()
    {
        var col = MakeColumnPlan("Status", "tinyint", 1, isPrimaryKey: true, generator: "Random.Byte");
        Assert.Equal(256, PlanGenerator.MaxCardinalityForColumn(col));
    }

    [Theory]
    [InlineData("int", 4)]
    [InlineData("bigint", 8)]
    [InlineData("varchar", 50)]
    [InlineData("nvarchar", 100)]
    [InlineData("uniqueidentifier", 16)]
    public void MaxCardinalityForColumn_WideTypes_ReturnsNull(string sqlType, int maxLength)
    {
        var col = MakeColumnPlan("Id", sqlType, maxLength, isPrimaryKey: true, generator: "Random.Int");
        Assert.Null(PlanGenerator.MaxCardinalityForColumn(col));
    }

    [Fact]
    public void MaxCardinalityForColumn_ZeroMaxLength_ReturnsNull()
    {
        var col = MakeColumnPlan("Code", "char", 0, isPrimaryKey: true);
        Assert.Null(PlanGenerator.MaxCardinalityForColumn(col));
    }

    // ──────────────────────────────────────────────
    // ComputeMaxDistinctRows — single PK column
    // ──────────────────────────────────────────────

    [Fact]
    public void ComputeMaxDistinctRows_Char1Pk_Returns36()
    {
        var tablePlan = MakeTablePlan("dbo.Narrow", 100,
        [
            MakeColumnPlan("Code", "char", 1, isPrimaryKey: true),
            MakeColumnPlan("Name", "varchar", 50)
        ]);

        var result = PlanGenerator.ComputeMaxDistinctRows(tablePlan, MakeTableInfo("dbo", "Narrow"));
        Assert.Equal(36, result);
    }

    [Fact]
    public void ComputeMaxDistinctRows_IdentityPk_ReturnsNull()
    {
        var idCol = MakeColumnPlan("Id", "int", 4, isPrimaryKey: true, generator: "skip");
        var tablePlan = MakeTablePlan("dbo.Wide", 100, [idCol]);

        var result = PlanGenerator.ComputeMaxDistinctRows(tablePlan, MakeTableInfo("dbo", "Wide"));
        Assert.Null(result);
    }

    [Fact]
    public void ComputeMaxDistinctRows_IntPk_ReturnsNull()
    {
        var col = MakeColumnPlan("Id", "int", 4, isPrimaryKey: true, generator: "Random.Int");
        var tablePlan = MakeTablePlan("dbo.Wide", 100, [col]);

        var result = PlanGenerator.ComputeMaxDistinctRows(tablePlan, MakeTableInfo("dbo", "Wide"));
        Assert.Null(result);
    }

    // ──────────────────────────────────────────────
    // ComputeMaxDistinctRows — composite PK
    // ──────────────────────────────────────────────

    [Fact]
    public void ComputeMaxDistinctRows_CompositePk_MultipliesCardinalities()
    {
        var tablePlan = MakeTablePlan("dbo.Composite", 5000,
        [
            MakeColumnPlan("A", "char", 1, isPrimaryKey: true),
            MakeColumnPlan("B", "char", 1, isPrimaryKey: true)
        ]);

        // 36 * 36 = 1296
        var result = PlanGenerator.ComputeMaxDistinctRows(tablePlan, MakeTableInfo("dbo", "Composite"));
        Assert.Equal(1296, result);
    }

    [Fact]
    public void ComputeMaxDistinctRows_CompositePk_OneUnbounded_ReturnsNull()
    {
        var tablePlan = MakeTablePlan("dbo.Mixed", 100,
        [
            MakeColumnPlan("Code", "char", 1, isPrimaryKey: true),
            MakeColumnPlan("Id", "int", 4, isPrimaryKey: true, generator: "Random.Int")
        ]);

        // int has unknown cardinality → composite is unbounded
        var result = PlanGenerator.ComputeMaxDistinctRows(tablePlan, MakeTableInfo("dbo", "Mixed"));
        Assert.Null(result);
    }

    // ──────────────────────────────────────────────
    // ComputeMaxDistinctRows — unique columns
    // ──────────────────────────────────────────────

    [Fact]
    public void ComputeMaxDistinctRows_UniqueChar1Column_Returns36()
    {
        var tablePlan = MakeTablePlan("dbo.UniqueNarrow", 100,
        [
            MakeColumnPlan("Id", "int", 4, isPrimaryKey: true, generator: "skip"),
            MakeColumnPlan("Code", "char", 1, isUnique: true)
        ]);

        var result = PlanGenerator.ComputeMaxDistinctRows(tablePlan, MakeTableInfo("dbo", "UniqueNarrow"));
        Assert.Equal(36, result);
    }

    [Fact]
    public void ComputeMaxDistinctRows_PkAndUnique_TakesMinimum()
    {
        var tablePlan = MakeTablePlan("dbo.Both", 100,
        [
            MakeColumnPlan("Code", "char", 2, isPrimaryKey: true),   // 36^2 = 1296
            MakeColumnPlan("Tag", "char", 1, isUnique: true)         // 36
        ]);

        var result = PlanGenerator.ComputeMaxDistinctRows(tablePlan, MakeTableInfo("dbo", "Both"));
        Assert.Equal(36, result);
    }

    // ──────────────────────────────────────────────
    // ComputeMaxDistinctRows — unique constraints (multi-column)
    // ──────────────────────────────────────────────

    [Fact]
    public void ComputeMaxDistinctRows_UniqueConstraint_MultipliesColumns()
    {
        var tablePlan = MakeTablePlan("dbo.MultiUq", 5000,
        [
            MakeColumnPlan("Id", "int", 4, isPrimaryKey: true, generator: "skip"),
            MakeColumnPlan("A", "char", 1),
            MakeColumnPlan("B", "char", 1)
        ],
        [
            new UniqueConstraintPlan { Name = "UQ_AB", Columns = ["A", "B"] }
        ]);

        // 36 * 36 = 1296
        var result = PlanGenerator.ComputeMaxDistinctRows(tablePlan, MakeTableInfo("dbo", "MultiUq"));
        Assert.Equal(1296, result);
    }

    [Fact]
    public void ComputeMaxDistinctRows_UniqueConstraintWithSkipColumn_Ignored()
    {
        var tablePlan = MakeTablePlan("dbo.UqSkip", 100,
        [
            MakeColumnPlan("Id", "int", 4, isPrimaryKey: true, generator: "skip"),
            MakeColumnPlan("A", "char", 1),
            MakeColumnPlan("Computed", "int", 4, generator: "skip")
        ],
        [
            new UniqueConstraintPlan { Name = "UQ_AC", Columns = ["A", "Computed"] }
        ]);

        // Constraint contains a skip column → not bounded by this constraint
        // But A itself is not marked IsUnique, so no limit
        var result = PlanGenerator.ComputeMaxDistinctRows(tablePlan, MakeTableInfo("dbo", "UqSkip"));
        Assert.Null(result);
    }

    // ──────────────────────────────────────────────
    // PlanGenerator.Generate — end-to-end capping
    // ──────────────────────────────────────────────

    [Fact]
    public void Generate_CapsRowCountForNarrowPk()
    {
        var table = new TableInfo
        {
            Schema = "dbo",
            TableName = "NarrowPk",
            Columns =
            [
                new ColumnInfo { Name = "Code", SqlType = "char", MaxLength = 1, IsPrimaryKey = true },
                new ColumnInfo { Name = "Description", SqlType = "varchar", MaxLength = 100 }
            ],
            PrimaryKeyColumns = ["Code"]
        };

        var planGen = new PlanGenerator();
        var plan = planGen.Generate([table], new HashSet<string>(), defaultRowCount: 100, seed: 42);

        var tablePlan = plan.Tables.Single();
        Assert.Equal(36, tablePlan.RowCount);
    }

    [Fact]
    public void Generate_DoesNotCapWhenRowCountFits()
    {
        var table = new TableInfo
        {
            Schema = "dbo",
            TableName = "NarrowPkSmall",
            Columns =
            [
                new ColumnInfo { Name = "Code", SqlType = "char", MaxLength = 1, IsPrimaryKey = true },
                new ColumnInfo { Name = "Description", SqlType = "varchar", MaxLength = 100 }
            ],
            PrimaryKeyColumns = ["Code"]
        };

        var planGen = new PlanGenerator();
        var plan = planGen.Generate([table], new HashSet<string>(), defaultRowCount: 10, seed: 42);

        var tablePlan = plan.Tables.Single();
        Assert.Equal(10, tablePlan.RowCount);
    }

    [Fact]
    public void Generate_DoesNotCapForWidePk()
    {
        var table = new TableInfo
        {
            Schema = "dbo",
            TableName = "WidePk",
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", MaxLength = 4, IsPrimaryKey = true },
                new ColumnInfo { Name = "Name", SqlType = "varchar", MaxLength = 100 }
            ],
            PrimaryKeyColumns = ["Id"]
        };

        var planGen = new PlanGenerator();
        var plan = planGen.Generate([table], new HashSet<string>(), defaultRowCount: 1000, seed: 42);

        var tablePlan = plan.Tables.Single();
        Assert.Equal(1000, tablePlan.RowCount);
    }

    [Fact]
    public void Generate_CapsForNarrowUniqueColumn()
    {
        var table = new TableInfo
        {
            Schema = "dbo",
            TableName = "NarrowUnique",
            Columns =
            [
                new ColumnInfo { Name = "Id", SqlType = "int", MaxLength = 4, IsPrimaryKey = true, IsIdentity = true },
                new ColumnInfo { Name = "Code", SqlType = "char", MaxLength = 1, IsUnique = true }
            ],
            PrimaryKeyColumns = ["Id"]
        };

        var planGen = new PlanGenerator();
        var plan = planGen.Generate([table], new HashSet<string>(), defaultRowCount: 100, seed: 42);

        var tablePlan = plan.Tables.Single();
        Assert.Equal(36, tablePlan.RowCount);
    }

    // ──────────────────────────────────────────────
    // DataInserter.GenerateRows — succeeds after capping
    // ──────────────────────────────────────────────

    [Fact]
    public void GenerateRows_Char1Pk_SucceedsWithCappedPlan()
    {
        var table = new TableInfo
        {
            Schema = "dbo",
            TableName = "SmallPk",
            Columns =
            [
                new ColumnInfo { Name = "Code", SqlType = "char", MaxLength = 1, IsPrimaryKey = true },
                new ColumnInfo { Name = "Value", SqlType = "int", MaxLength = 4 }
            ],
            PrimaryKeyColumns = ["Code"]
        };

        var planGen = new PlanGenerator();
        var plan = planGen.Generate([table], new HashSet<string>(), defaultRowCount: 100, seed: 42);

        var tablePlan = plan.Tables.Single();
        Assert.Equal(36, tablePlan.RowCount);

        var valueGen = new ColumnValueGenerator(seed: 42);
        var inserter = new DataInserter("unused", valueGen, new HashSet<string>());

        var result = inserter.GenerateRows(tablePlan);
        Assert.Equal(36, result.DataTable.Rows.Count);
    }

    [Fact]
    public void GenerateRows_Char1Pk_UncappedRowCount_ThrowsWithDetailedMessage()
    {
        var tablePlan = MakeTablePlan("dbo.TooMany", 100,
        [
            MakeColumnPlan("Code", "char", 1, isPrimaryKey: true),
            MakeColumnPlan("Value", "int", 4, generator: "Random.Int")
        ]);

        var valueGen = new ColumnValueGenerator(seed: 42);
        var inserter = new DataInserter("unused", valueGen, new HashSet<string>());

        var ex = Assert.Throws<DataGenerationException>(() => inserter.GenerateRows(tablePlan));
        var inner = ex.InnerException;
        Assert.NotNull(inner);
        Assert.Contains("dbo.TooMany", inner.Message);
        Assert.Contains("Narrow unique/PK columns", inner.Message);
        Assert.Contains("Code", inner.Message);
    }
}
