using Microsoft.Data.SqlClient;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;

namespace SyntheticDataGenerator.Tests;

[Collection("Database")]
public class IntegrationTests
{
    private readonly DatabaseFixture _fixture;
    private const int RowCount = 10;
    private const int Seed = 42;

    public IntegrationTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    // ──────────────────────────────────────────────
    // Helper: run the generator pipeline for a given set of table names
    // ──────────────────────────────────────────────

    private async Task<Dictionary<string, int>> GenerateDataAsync(params string[] tableNames)
    {
        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();

        var nameSet = new HashSet<string>(tableNames, StringComparer.OrdinalIgnoreCase);
        var tables = allTables
            .Where(t => nameSet.Contains(t.TableName) || nameSet.Contains(t.FullName))
            .ToList();

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var valueGen = new ColumnValueGenerator(seed: Seed);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, graph.SelfReferencingTables);

        var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in sorted)
        {
            var inserted = await inserter.InsertTableAsync(table, RowCount);
            results[table.FullName] = inserted;
        }

        return results;
    }

    // ══════════════════════════════════════════════
    //  1. Integer Types
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test01_IntegerTypes()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestIntegerTypes (
                Id          INT IDENTITY(1,1) PRIMARY KEY,
                ColInt      INT          NOT NULL,
                ColBigInt   BIGINT       NOT NULL,
                ColSmallInt SMALLINT     NOT NULL,
                ColTinyInt  TINYINT      NOT NULL,
                ColBit      BIT          NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestIntegerTypes");
        Assert.Equal(RowCount, results["dbo.TestIntegerTypes"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestIntegerTypes");
        Assert.Equal(RowCount, rows.Count);

        foreach (var row in rows)
        {
            Assert.IsType<int>(row["ColInt"]);
            Assert.IsType<long>(row["ColBigInt"]);
            Assert.IsType<short>(row["ColSmallInt"]);
            Assert.IsType<byte>(row["ColTinyInt"]);
            Assert.IsType<bool>(row["ColBit"]);
        }
    }

    // ══════════════════════════════════════════════
    //  2. Decimal and Money Types
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test02_DecimalAndMoneyTypes()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestDecimalMoney (
                Id            INT IDENTITY(1,1) PRIMARY KEY,
                ColDecimal    DECIMAL(18,4) NOT NULL,
                ColNumeric    NUMERIC(10,2) NOT NULL,
                ColMoney      MONEY         NOT NULL,
                ColSmallMoney SMALLMONEY    NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestDecimalMoney");
        Assert.Equal(RowCount, results["dbo.TestDecimalMoney"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestDecimalMoney");
        foreach (var row in rows)
        {
            Assert.IsType<decimal>(row["ColDecimal"]);
            Assert.IsType<decimal>(row["ColNumeric"]);
            Assert.IsType<decimal>(row["ColMoney"]);
            Assert.IsType<decimal>(row["ColSmallMoney"]);
        }
    }

    // ══════════════════════════════════════════════
    //  2b. Tight-Precision Decimal/Numeric
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test02b_TightPrecisionDecimal()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestTightDecimal (
                Id           INT IDENTITY(1,1) PRIMARY KEY,
                ColDec5_2    DECIMAL(5,2)   NOT NULL,
                ColDec3_0    DECIMAL(3,0)   NOT NULL,
                ColDec7_4    NUMERIC(7,4)   NOT NULL,
                ColDec4_3    DECIMAL(4,3)   NOT NULL,
                ColSmMoney   SMALLMONEY     NOT NULL,
                ColPrice     DECIMAL(6,2)   NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestTightDecimal");
        Assert.Equal(RowCount, results["dbo.TestTightDecimal"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestTightDecimal");
        foreach (var row in rows)
        {
            var dec5_2 = (decimal)row["ColDec5_2"]!;
            Assert.InRange(dec5_2, -999.99m, 999.99m);

            var dec3_0 = (decimal)row["ColDec3_0"]!;
            Assert.InRange(dec3_0, -999m, 999m);

            var dec7_4 = (decimal)row["ColDec7_4"]!;
            Assert.InRange(dec7_4, -999.9999m, 999.9999m);

            var dec4_3 = (decimal)row["ColDec4_3"]!;
            Assert.InRange(dec4_3, -9.999m, 9.999m);

            Assert.IsType<decimal>(row["ColSmMoney"]);

            var price = (decimal)row["ColPrice"]!;
            Assert.InRange(price, -9999.99m, 9999.99m);
        }
    }

    // ══════════════════════════════════════════════
    //  2c. Tight-Precision Decimal with Name Heuristics
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test02c_TightPrecisionDecimalWithNameHeuristics()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestTightDecimalNames (
                Id           INT IDENTITY(1,1) PRIMARY KEY,
                price        DECIMAL(5,2)   NOT NULL,
                amount       NUMERIC(6,2)   NOT NULL,
                cost         DECIMAL(4,2)   NOT NULL,
                salary       DECIMAL(7,2)   NOT NULL,
                quantity     DECIMAL(3,0)   NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestTightDecimalNames");
        Assert.Equal(RowCount, results["dbo.TestTightDecimalNames"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestTightDecimalNames");
        foreach (var row in rows)
        {
            var price = (decimal)row["price"]!;
            Assert.InRange(price, -999.99m, 999.99m);

            var amount = (decimal)row["amount"]!;
            Assert.InRange(amount, -9999.99m, 9999.99m);

            var cost = (decimal)row["cost"]!;
            Assert.InRange(cost, -99.99m, 99.99m);

            var salary = (decimal)row["salary"]!;
            Assert.InRange(salary, -99999.99m, 99999.99m);

            var quantity = (decimal)row["quantity"]!;
            Assert.InRange(quantity, -999m, 999m);
        }
    }

    // ══════════════════════════════════════════════
    //  3. Floating Point Types
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test03_FloatingPointTypes()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestFloatingPoint (
                Id       INT IDENTITY(1,1) PRIMARY KEY,
                ColFloat FLOAT NOT NULL,
                ColReal  REAL  NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestFloatingPoint");
        Assert.Equal(RowCount, results["dbo.TestFloatingPoint"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestFloatingPoint");
        foreach (var row in rows)
        {
            Assert.IsType<double>(row["ColFloat"]);
            Assert.IsType<float>(row["ColReal"]);
        }
    }

    // ══════════════════════════════════════════════
    //  4. Date/Time Types
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test04_DateTimeTypes()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestDateTimeTypes (
                Id               INT IDENTITY(1,1) PRIMARY KEY,
                ColDatetime      DATETIME        NOT NULL,
                ColDatetime2     DATETIME2       NOT NULL,
                ColSmallDatetime SMALLDATETIME   NOT NULL,
                ColDate          DATE            NOT NULL,
                ColTime          TIME            NOT NULL,
                ColDatetimeOff   DATETIMEOFFSET  NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestDateTimeTypes");
        Assert.Equal(RowCount, results["dbo.TestDateTimeTypes"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestDateTimeTypes");
        Assert.Equal(RowCount, rows.Count);

        foreach (var row in rows)
        {
            Assert.IsType<DateTime>(row["ColDatetime"]);
            Assert.IsType<DateTime>(row["ColDatetime2"]);
            Assert.IsType<DateTime>(row["ColSmallDatetime"]);
            Assert.IsType<DateTime>(row["ColDate"]);
            Assert.IsType<TimeSpan>(row["ColTime"]);
            Assert.IsType<DateTimeOffset>(row["ColDatetimeOff"]);
        }
    }

    // ══════════════════════════════════════════════
    //  5. String Types (varchar / nvarchar / text / ntext)
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test05_StringTypesVarchar()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestStringVarchar (
                Id          INT IDENTITY(1,1) PRIMARY KEY,
                ColVarchar  VARCHAR(50)   NOT NULL,
                ColNvarchar NVARCHAR(100) NOT NULL,
                ColText     TEXT          NOT NULL,
                ColNtext    NTEXT         NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestStringVarchar");
        Assert.Equal(RowCount, results["dbo.TestStringVarchar"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestStringVarchar");
        foreach (var row in rows)
        {
            var varchar = Assert.IsType<string>(row["ColVarchar"]);
            Assert.InRange(varchar.Length, 1, 50);

            var nvarchar = Assert.IsType<string>(row["ColNvarchar"]);
            Assert.InRange(nvarchar.Length, 1, 100);

            Assert.IsType<string>(row["ColText"]);
            Assert.IsType<string>(row["ColNtext"]);
        }
    }

    // ══════════════════════════════════════════════
    //  6. Fixed-Length String Types (char / nchar)
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test06_StringTypesFixedLength()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestStringFixed (
                Id       INT IDENTITY(1,1) PRIMARY KEY,
                ColChar  CHAR(10)  NOT NULL,
                ColNchar NCHAR(5)  NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestStringFixed");
        Assert.Equal(RowCount, results["dbo.TestStringFixed"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestStringFixed");
        foreach (var row in rows)
        {
            var charVal = Assert.IsType<string>(row["ColChar"]);
            Assert.Equal(10, charVal.Length); // CHAR pads to fixed length

            var ncharVal = Assert.IsType<string>(row["ColNchar"]);
            Assert.Equal(5, ncharVal.Length);
        }
    }

    // ══════════════════════════════════════════════
    //  7. Binary and GUID Types
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test07_BinaryAndGuidTypes()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestBinaryGuid (
                Id        INT IDENTITY(1,1) PRIMARY KEY,
                ColVarbin VARBINARY(100)     NOT NULL,
                ColBin    BINARY(16)         NOT NULL,
                ColImage  IMAGE              NOT NULL,
                ColGuid   UNIQUEIDENTIFIER   NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestBinaryGuid");
        Assert.Equal(RowCount, results["dbo.TestBinaryGuid"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestBinaryGuid");
        foreach (var row in rows)
        {
            Assert.IsType<byte[]>(row["ColVarbin"]);
            var bin = Assert.IsType<byte[]>(row["ColBin"]);
            Assert.Equal(16, bin.Length);
            Assert.IsType<byte[]>(row["ColImage"]);
            Assert.IsType<Guid>(row["ColGuid"]);
        }
    }

    // ══════════════════════════════════════════════
    //  8. XML Type
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test08_XmlType()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestXmlType (
                Id     INT IDENTITY(1,1) PRIMARY KEY,
                ColXml XML NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestXmlType");
        Assert.Equal(RowCount, results["dbo.TestXmlType"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT CAST(ColXml AS NVARCHAR(MAX)) AS ColXml FROM dbo.TestXmlType");
        foreach (var row in rows)
        {
            var xml = Assert.IsType<string>(row["ColXml"]);
            Assert.StartsWith("<data>", xml);
            Assert.EndsWith("</data>", xml);
        }
    }

    // ══════════════════════════════════════════════
    //  9. Identity Primary Key
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test09_IdentityPrimaryKey()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestIdentityPK (
                Id   INT IDENTITY(1,1) PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestIdentityPK");
        Assert.Equal(RowCount, results["dbo.TestIdentityPK"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT Id FROM dbo.TestIdentityPK ORDER BY Id");
        var ids = rows.Select(r => (int)r["Id"]!).ToList();

        // Identity should produce sequential values starting at 1
        Assert.Equal(RowCount, ids.Count);
        Assert.Equal(1, ids.First());
        Assert.Equal(RowCount, ids.Last());
    }

    // ══════════════════════════════════════════════
    // 10. Composite Primary Key
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test10_CompositePrimaryKey()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestCompositePK (
                KeyPart1  INT NOT NULL,
                KeyPart2  INT NOT NULL,
                DataValue NVARCHAR(50) NOT NULL,
                PRIMARY KEY (KeyPart1, KeyPart2)
            )
            """);

        var results = await GenerateDataAsync("TestCompositePK");
        Assert.Equal(RowCount, results["dbo.TestCompositePK"]);

        var count = (int)(await _fixture.ExecuteScalarAsync("SELECT COUNT(*) FROM dbo.TestCompositePK"))!;
        Assert.Equal(RowCount, count);

        // Verify PK uniqueness
        var uniqueCount = (int)(await _fixture.ExecuteScalarAsync(
            "SELECT COUNT(DISTINCT CONCAT(KeyPart1, '-', KeyPart2)) FROM dbo.TestCompositePK"))!;
        Assert.Equal(RowCount, uniqueCount);
    }

    // ══════════════════════════════════════════════
    // 11. Nullable Columns
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test11_NullableColumns()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestNullable (
                Id       INT IDENTITY(1,1) PRIMARY KEY,
                ColA     NVARCHAR(50)  NULL,
                ColB     INT           NULL,
                ColC     DATETIME      NULL,
                ColD     DECIMAL(10,2) NULL,
                ColE     BIT           NULL
            )
            """);

        // Use more rows to give the 10% null chance a fair shot
        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var table = allTables.First(t => t.TableName == "TestNullable");

        var graph = new DependencyGraph();
        graph.Build([table]);
        var sorted = graph.GetTopologicalOrder();

        var valueGen = new ColumnValueGenerator(seed: Seed);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, graph.SelfReferencingTables);
        var inserted = await inserter.InsertTableAsync(table, 100);
        Assert.Equal(100, inserted);

        // With 5 nullable columns and 100 rows (500 opportunities), expect at least some NULLs
        var nullCount = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT SUM(
                CASE WHEN ColA IS NULL THEN 1 ELSE 0 END +
                CASE WHEN ColB IS NULL THEN 1 ELSE 0 END +
                CASE WHEN ColC IS NULL THEN 1 ELSE 0 END +
                CASE WHEN ColD IS NULL THEN 1 ELSE 0 END +
                CASE WHEN ColE IS NULL THEN 1 ELSE 0 END
            ) FROM dbo.TestNullable
            """))!;
        Assert.True(nullCount > 0, "Expected at least some NULL values in nullable columns");
    }

    // ══════════════════════════════════════════════
    // 12. Non-Nullable Columns
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test12_NonNullableColumns()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestNonNullable (
                Id   INT IDENTITY(1,1) PRIMARY KEY,
                ColA NVARCHAR(50)  NOT NULL,
                ColB INT           NOT NULL,
                ColC DATETIME      NOT NULL,
                ColD DECIMAL(10,2) NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestNonNullable");
        Assert.Equal(RowCount, results["dbo.TestNonNullable"]);

        var nullCount = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT SUM(
                CASE WHEN ColA IS NULL THEN 1 ELSE 0 END +
                CASE WHEN ColB IS NULL THEN 1 ELSE 0 END +
                CASE WHEN ColC IS NULL THEN 1 ELSE 0 END +
                CASE WHEN ColD IS NULL THEN 1 ELSE 0 END
            ) FROM dbo.TestNonNullable
            """))!;
        Assert.Equal(0, nullCount);
    }

    // ══════════════════════════════════════════════
    // 13. Computed Column
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test13_ComputedColumn()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestComputed (
                Id       INT IDENTITY(1,1) PRIMARY KEY,
                ColA     INT NOT NULL,
                ColB     INT NOT NULL,
                ColSum   AS (ColA + ColB)
            )
            """);

        var results = await GenerateDataAsync("TestComputed");
        Assert.Equal(RowCount, results["dbo.TestComputed"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT ColA, ColB, ColSum FROM dbo.TestComputed");
        foreach (var row in rows)
        {
            var a = (int)row["ColA"]!;
            var b = (int)row["ColB"]!;
            var sum = (int)row["ColSum"]!;
            Assert.Equal(a + b, sum);
        }
    }

    // ══════════════════════════════════════════════
    // 14. Simple Foreign Key
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test14_SimpleForeignKey()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestFKParent (
                ParentId INT IDENTITY(1,1) PRIMARY KEY,
                Label    NVARCHAR(50) NOT NULL
            )
            """);

        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestFKChild (
                ChildId  INT IDENTITY(1,1) PRIMARY KEY,
                ParentId INT NOT NULL,
                Value    NVARCHAR(50) NOT NULL,
                CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.TestFKParent(ParentId)
            )
            """);

        var results = await GenerateDataAsync("TestFKParent", "TestFKChild");
        Assert.Equal(RowCount, results["dbo.TestFKParent"]);
        Assert.Equal(RowCount, results["dbo.TestFKChild"]);

        // Every child.ParentId must exist in parent
        var orphans = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*) FROM dbo.TestFKChild c
            WHERE NOT EXISTS (SELECT 1 FROM dbo.TestFKParent p WHERE p.ParentId = c.ParentId)
            """))!;
        Assert.Equal(0, orphans);
    }

    // ══════════════════════════════════════════════
    // 15. Self-Referencing Foreign Key
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test15_SelfReferencingFK()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestSelfRef (
                EmployeeId INT IDENTITY(1,1) PRIMARY KEY,
                Name       NVARCHAR(50) NOT NULL,
                ManagerId  INT NULL,
                CONSTRAINT FK_SelfRef FOREIGN KEY (ManagerId) REFERENCES dbo.TestSelfRef(EmployeeId)
            )
            """);

        var results = await GenerateDataAsync("TestSelfRef");
        Assert.Equal(RowCount, results["dbo.TestSelfRef"]);

        var rows = await _fixture.ExecuteQueryAsync(
            "SELECT EmployeeId, ManagerId FROM dbo.TestSelfRef");

        var allIds = rows.Select(r => (int)r["EmployeeId"]!).ToHashSet();

        // Some rows should have NULL ManagerId (roots)
        var nullManagers = rows.Count(r => r["ManagerId"] is null);
        Assert.True(nullManagers > 0, "Expected at least one root row with NULL ManagerId");

        // Non-null ManagerId values must reference existing EmployeeIds
        foreach (var row in rows.Where(r => r["ManagerId"] is not null))
        {
            var managerId = (int)row["ManagerId"]!;
            Assert.Contains(managerId, allIds);
        }
    }

    // ══════════════════════════════════════════════
    // 16. Multiple Foreign Keys
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test16_MultipleForeignKeys()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestMultiFKCategory (
                CategoryId INT IDENTITY(1,1) PRIMARY KEY,
                CatName    NVARCHAR(50) NOT NULL
            )
            """);

        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestMultiFKSupplier (
                SupplierId INT IDENTITY(1,1) PRIMARY KEY,
                SupName    NVARCHAR(50) NOT NULL
            )
            """);

        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestMultiFKProduct (
                ProductId  INT IDENTITY(1,1) PRIMARY KEY,
                CategoryId INT NOT NULL,
                SupplierId INT NOT NULL,
                ProdName   NVARCHAR(50) NOT NULL,
                CONSTRAINT FK_Prod_Cat  FOREIGN KEY (CategoryId) REFERENCES dbo.TestMultiFKCategory(CategoryId),
                CONSTRAINT FK_Prod_Sup  FOREIGN KEY (SupplierId) REFERENCES dbo.TestMultiFKSupplier(SupplierId)
            )
            """);

        var results = await GenerateDataAsync("TestMultiFKCategory", "TestMultiFKSupplier", "TestMultiFKProduct");
        Assert.Equal(RowCount, results["dbo.TestMultiFKCategory"]);
        Assert.Equal(RowCount, results["dbo.TestMultiFKSupplier"]);
        Assert.Equal(RowCount, results["dbo.TestMultiFKProduct"]);

        var orphanCats = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*) FROM dbo.TestMultiFKProduct p
            WHERE NOT EXISTS (SELECT 1 FROM dbo.TestMultiFKCategory c WHERE c.CategoryId = p.CategoryId)
            """))!;
        Assert.Equal(0, orphanCats);

        var orphanSups = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*) FROM dbo.TestMultiFKProduct p
            WHERE NOT EXISTS (SELECT 1 FROM dbo.TestMultiFKSupplier s WHERE s.SupplierId = p.SupplierId)
            """))!;
        Assert.Equal(0, orphanSups);
    }

    // ══════════════════════════════════════════════
    // 17. Chained Foreign Keys (A -> B -> C)
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test17_ChainedForeignKeys()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestChainC (
                CId   INT IDENTITY(1,1) PRIMARY KEY,
                CName NVARCHAR(50) NOT NULL
            )
            """);

        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestChainB (
                BId   INT IDENTITY(1,1) PRIMARY KEY,
                CId   INT NOT NULL,
                BName NVARCHAR(50) NOT NULL,
                CONSTRAINT FK_B_C FOREIGN KEY (CId) REFERENCES dbo.TestChainC(CId)
            )
            """);

        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestChainA (
                AId   INT IDENTITY(1,1) PRIMARY KEY,
                BId   INT NOT NULL,
                AName NVARCHAR(50) NOT NULL,
                CONSTRAINT FK_A_B FOREIGN KEY (BId) REFERENCES dbo.TestChainB(BId)
            )
            """);

        var results = await GenerateDataAsync("TestChainA", "TestChainB", "TestChainC");

        // All three tables should be populated
        Assert.Equal(RowCount, results["dbo.TestChainC"]);
        Assert.Equal(RowCount, results["dbo.TestChainB"]);
        Assert.Equal(RowCount, results["dbo.TestChainA"]);

        // Verify FK integrity through the chain
        var brokenBC = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*) FROM dbo.TestChainB b
            WHERE NOT EXISTS (SELECT 1 FROM dbo.TestChainC c WHERE c.CId = b.CId)
            """))!;
        Assert.Equal(0, brokenBC);

        var brokenAB = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*) FROM dbo.TestChainA a
            WHERE NOT EXISTS (SELECT 1 FROM dbo.TestChainB b WHERE b.BId = a.BId)
            """))!;
        Assert.Equal(0, brokenAB);
    }

    // ══════════════════════════════════════════════
    // 18. Name-Based Heuristics
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test18_NameBasedHeuristics()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestNameHeuristics (
                Id         INT IDENTITY(1,1) PRIMARY KEY,
                first_name NVARCHAR(100) NOT NULL,
                last_name  NVARCHAR(100) NOT NULL,
                email      NVARCHAR(200) NOT NULL,
                phone      NVARCHAR(50)  NOT NULL,
                city       NVARCHAR(100) NOT NULL,
                zip_code   NVARCHAR(20)  NOT NULL,
                price      DECIMAL(10,2) NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestNameHeuristics");
        Assert.Equal(RowCount, results["dbo.TestNameHeuristics"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestNameHeuristics");
        foreach (var row in rows)
        {
            var email = (string)row["email"]!;
            Assert.Contains("@", email);

            var firstName = (string)row["first_name"]!;
            Assert.False(string.IsNullOrWhiteSpace(firstName));

            var lastName = (string)row["last_name"]!;
            Assert.False(string.IsNullOrWhiteSpace(lastName));

            var price = (decimal)row["price"]!;
            Assert.InRange(price, 0m, 100000m);
        }
    }

    // ══════════════════════════════════════════════
    // 19. Max-Length-One Varchar
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test19_MaxLengthOneVarchar()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestMaxLen1 (
                Id       INT IDENTITY(1,1) PRIMARY KEY,
                ColV1    VARCHAR(1)  NOT NULL,
                ColNV1   NVARCHAR(1) NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestMaxLen1");
        Assert.Equal(RowCount, results["dbo.TestMaxLen1"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT ColV1, ColNV1 FROM dbo.TestMaxLen1");
        foreach (var row in rows)
        {
            var v1 = (string)row["ColV1"]!;
            Assert.Equal(1, v1.Length);

            var nv1 = (string)row["ColNV1"]!;
            Assert.Equal(1, nv1.Length);
        }
    }

    // ══════════════════════════════════════════════
    // 20. Table with Only Identity PK (DEFAULT VALUES)
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test20_TableWithOnlyIdentityPK()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestOnlyIdentity (
                Id INT IDENTITY(1,1) PRIMARY KEY
            )
            """);

        var results = await GenerateDataAsync("TestOnlyIdentity");
        Assert.Equal(RowCount, results["dbo.TestOnlyIdentity"]);

        var count = (int)(await _fixture.ExecuteScalarAsync("SELECT COUNT(*) FROM dbo.TestOnlyIdentity"))!;
        Assert.Equal(RowCount, count);

        // Verify identity values are sequential
        var rows = await _fixture.ExecuteQueryAsync("SELECT Id FROM dbo.TestOnlyIdentity ORDER BY Id");
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(i + 1, (int)rows[i]["Id"]!);
        }
    }

    // ══════════════════════════════════════════════
    // 21. Composite Foreign Key
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test21_CompositeForeignKey()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestCompFKParent (
                KeyA INT     NOT NULL,
                KeyB INT     NOT NULL,
                Label NVARCHAR(50) NOT NULL,
                PRIMARY KEY (KeyA, KeyB)
            )
            """);

        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestCompFKChild (
                ChildId INT IDENTITY(1,1) PRIMARY KEY,
                RefA    INT NOT NULL,
                RefB    INT NOT NULL,
                Value   NVARCHAR(50) NOT NULL,
                CONSTRAINT FK_CompChild_Parent FOREIGN KEY (RefA, RefB)
                    REFERENCES dbo.TestCompFKParent(KeyA, KeyB)
            )
            """);

        var results = await GenerateDataAsync("TestCompFKParent", "TestCompFKChild");
        Assert.Equal(RowCount, results["dbo.TestCompFKParent"]);
        Assert.Equal(RowCount, results["dbo.TestCompFKChild"]);

        var orphans = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*) FROM dbo.TestCompFKChild c
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.TestCompFKParent p
                WHERE p.KeyA = c.RefA AND p.KeyB = c.RefB
            )
            """))!;
        Assert.Equal(0, orphans);
    }

    // ══════════════════════════════════════════════
    // 22. Bit columns with name-heuristic-triggering names
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test22a_BitColumnsWithMisleadingNames()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestBitNames (
                Id               INT IDENTITY(1,1) PRIMARY KEY,
                is_active_note   BIT NOT NULL,
                has_status       BIT NOT NULL,
                description      BIT NOT NULL,
                company          BIT NOT NULL,
                status           BIT NOT NULL,
                title            BIT NOT NULL,
                city             BIT NULL
            )
            """);

        var results = await GenerateDataAsync("TestBitNames");
        Assert.Equal(RowCount, results["dbo.TestBitNames"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestBitNames");
        foreach (var row in rows)
        {
            Assert.IsType<bool>(row["is_active_note"]);
            Assert.IsType<bool>(row["has_status"]);
            Assert.IsType<bool>(row["description"]);
            Assert.IsType<bool>(row["company"]);
            Assert.IsType<bool>(row["status"]);
            Assert.IsType<bool>(row["title"]);
            if (row["city"] is not null)
                Assert.IsType<bool>(row["city"]);
        }
    }

    // ══════════════════════════════════════════════
    // 22b. Int/BigInt columns with name-heuristic-triggering names
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test22b_IntColumnsWithMisleadingNames()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestIntNames (
                Id          INT IDENTITY(1,1) PRIMARY KEY,
                status      INT    NOT NULL,
                description INT    NOT NULL,
                city        INT    NOT NULL,
                company     BIGINT NOT NULL,
                email       INT    NOT NULL,
                title       BIGINT NOT NULL,
                name        INT    NULL
            )
            """);

        var results = await GenerateDataAsync("TestIntNames");
        Assert.Equal(RowCount, results["dbo.TestIntNames"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestIntNames");
        foreach (var row in rows)
        {
            Assert.IsType<int>(row["status"]);
            Assert.IsType<int>(row["description"]);
            Assert.IsType<int>(row["city"]);
            Assert.IsType<long>(row["company"]);
            Assert.IsType<int>(row["email"]);
            Assert.IsType<long>(row["title"]);
            if (row["name"] is not null)
                Assert.IsType<int>(row["name"]);
        }
    }

    // ══════════════════════════════════════════════
    // 22c. DateTime2 columns with name-heuristic-triggering names
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test22c_DateTimeColumnsWithMisleadingNames()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestDateNames (
                Id          INT IDENTITY(1,1) PRIMARY KEY,
                status      DATETIME2    NOT NULL,
                description DATETIME2    NOT NULL,
                email       DATETIME     NOT NULL,
                city        DATE         NOT NULL,
                company     DATETIME2    NOT NULL,
                title       SMALLDATETIME NOT NULL,
                name        DATETIME2    NULL
            )
            """);

        var results = await GenerateDataAsync("TestDateNames");
        Assert.Equal(RowCount, results["dbo.TestDateNames"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestDateNames");
        foreach (var row in rows)
        {
            Assert.IsType<DateTime>(row["status"]);
            Assert.IsType<DateTime>(row["description"]);
            Assert.IsType<DateTime>(row["email"]);
            Assert.IsType<DateTime>(row["city"]);
            Assert.IsType<DateTime>(row["company"]);
            Assert.IsType<DateTime>(row["title"]);
            if (row["name"] is not null)
                Assert.IsType<DateTime>(row["name"]);
        }
    }

    // ══════════════════════════════════════════════
    // 22d. Decimal/Float columns with name-heuristic-triggering names
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test22d_NumericColumnsWithMisleadingNames()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestNumericNames (
                Id          INT IDENTITY(1,1) PRIMARY KEY,
                status      DECIMAL(10,2) NOT NULL,
                email       FLOAT         NOT NULL,
                city        MONEY         NOT NULL,
                description REAL          NOT NULL,
                company     NUMERIC(8,2)  NOT NULL,
                name        DECIMAL(10,2) NULL
            )
            """);

        var results = await GenerateDataAsync("TestNumericNames");
        Assert.Equal(RowCount, results["dbo.TestNumericNames"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestNumericNames");
        foreach (var row in rows)
        {
            Assert.IsType<decimal>(row["status"]);
            Assert.IsType<double>(row["email"]);
            Assert.IsType<decimal>(row["city"]);
            Assert.IsType<float>(row["description"]);
            Assert.IsType<decimal>(row["company"]);
            if (row["name"] is not null)
                Assert.IsType<decimal>(row["name"]);
        }
    }

    // ══════════════════════════════════════════════
    // 23. Non-Identity PK Duplicate Prevention
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test23_NonIdentityPkNoDuplicates()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestNonIdentityPK (
                Code NVARCHAR(10) NOT NULL PRIMARY KEY,
                Label NVARCHAR(50) NOT NULL
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var table = allTables.First(t => t.TableName == "TestNonIdentityPK");

        var graph = new DependencyGraph();
        graph.Build([table]);
        var sorted = graph.GetTopologicalOrder();

        var valueGen = new ColumnValueGenerator(seed: Seed);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, graph.SelfReferencingTables);

        var inserted = await inserter.InsertTableAsync(table, 50);
        Assert.Equal(50, inserted);

        var count = (int)(await _fixture.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM dbo.TestNonIdentityPK"))!;
        Assert.Equal(50, count);

        var distinctCount = (int)(await _fixture.ExecuteScalarAsync(
            "SELECT COUNT(DISTINCT Code) FROM dbo.TestNonIdentityPK"))!;
        Assert.Equal(50, distinctCount);
    }

    // ══════════════════════════════════════════════
    // 24. Non-Identity Composite PK Duplicate Prevention
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test24_NonIdentityCompositePkNoDuplicates()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestNonIdCompPK (
                PartA INT         NOT NULL,
                PartB NVARCHAR(10) NOT NULL,
                Value NVARCHAR(50) NOT NULL,
                PRIMARY KEY (PartA, PartB)
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var table = allTables.First(t => t.TableName == "TestNonIdCompPK");

        var graph = new DependencyGraph();
        graph.Build([table]);
        var sorted = graph.GetTopologicalOrder();

        var valueGen = new ColumnValueGenerator(seed: Seed);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, graph.SelfReferencingTables);

        var inserted = await inserter.InsertTableAsync(table, 50);
        Assert.Equal(50, inserted);

        var count = (int)(await _fixture.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM dbo.TestNonIdCompPK"))!;
        Assert.Equal(50, count);

        var distinctCount = (int)(await _fixture.ExecuteScalarAsync(
            "SELECT COUNT(DISTINCT CONCAT(PartA, '|', PartB)) FROM dbo.TestNonIdCompPK"))!;
        Assert.Equal(50, distinctCount);
    }

    // ══════════════════════════════════════════════
    // 25. Junction Table (All-FK Composite PK) Duplicate Prevention
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test25_JunctionTablePkNoDuplicates()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestJuncLeft (
                LeftId INT IDENTITY(1,1) PRIMARY KEY,
                Label  NVARCHAR(50) NOT NULL
            )
            """);

        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestJuncRight (
                RightId INT IDENTITY(1,1) PRIMARY KEY,
                Label   NVARCHAR(50) NOT NULL
            )
            """);

        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestJuncBridge (
                LeftId  INT NOT NULL,
                RightId INT NOT NULL,
                PRIMARY KEY (LeftId, RightId),
                CONSTRAINT FK_Junc_Left  FOREIGN KEY (LeftId)  REFERENCES dbo.TestJuncLeft(LeftId),
                CONSTRAINT FK_Junc_Right FOREIGN KEY (RightId) REFERENCES dbo.TestJuncRight(RightId)
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();

        var nameSet = new HashSet<string>(
            ["TestJuncLeft", "TestJuncRight", "TestJuncBridge"],
            StringComparer.OrdinalIgnoreCase);
        var tables = allTables.Where(t => nameSet.Contains(t.TableName)).ToList();

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var parentRowCount = 20;
        var bridgeRowCount = 15;

        var valueGen = new ColumnValueGenerator(seed: Seed);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, graph.SelfReferencingTables);

        foreach (var tbl in sorted)
        {
            var rows = tbl.TableName == "TestJuncBridge" ? bridgeRowCount : parentRowCount;
            await inserter.InsertTableAsync(tbl, rows);
        }

        var count = (int)(await _fixture.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM dbo.TestJuncBridge"))!;
        Assert.Equal(bridgeRowCount, count);

        var orphans = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*) FROM dbo.TestJuncBridge b
            WHERE NOT EXISTS (SELECT 1 FROM dbo.TestJuncLeft  l WHERE l.LeftId  = b.LeftId)
               OR NOT EXISTS (SELECT 1 FROM dbo.TestJuncRight r WHERE r.RightId = b.RightId)
            """))!;
        Assert.Equal(0, orphans);
    }

    // ══════════════════════════════════════════════
    // 22. Self-Referencing Composite Foreign Key
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test22_SelfReferencingCompositeForeignKey()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestCompSelfRef (
                KeyA      INT NOT NULL,
                KeyB      INT NOT NULL,
                ParentA   INT NULL,
                ParentB   INT NULL,
                Label     NVARCHAR(50) NOT NULL,
                PRIMARY KEY (KeyA, KeyB),
                CONSTRAINT FK_CompSelfRef FOREIGN KEY (ParentA, ParentB)
                    REFERENCES dbo.TestCompSelfRef(KeyA, KeyB)
            )
            """);

        var results = await GenerateDataAsync("TestCompSelfRef");
        Assert.Equal(RowCount, results["dbo.TestCompSelfRef"]);

        var rows = await _fixture.ExecuteQueryAsync(
            "SELECT KeyA, KeyB, ParentA, ParentB FROM dbo.TestCompSelfRef");

        var nullParents = rows.Count(r => r["ParentA"] is null || r["ParentA"] is DBNull);
        Assert.True(nullParents > 0, "Expected at least one root row with NULL parent");

        var orphans = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*) FROM dbo.TestCompSelfRef c
            WHERE c.ParentA IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.TestCompSelfRef p
                  WHERE p.KeyA = c.ParentA AND p.KeyB = c.ParentB
              )
            """))!;
        Assert.Equal(0, orphans);
    }

    // ══════════════════════════════════════════════
    // 26. Tight-Precision Decimal via Plan Execution
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test26_TightPrecisionDecimalViaPlan()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestTightDecPlan (
                Id           INT IDENTITY(1,1) PRIMARY KEY,
                ColDec5_2    DECIMAL(5,2)   NOT NULL,
                ColNum4_1    NUMERIC(4,1)   NOT NULL,
                ColSmMoney   SMALLMONEY     NOT NULL,
                price        DECIMAL(6,2)   NOT NULL
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestTightDecPlan").ToList();

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, RowCount, Seed, "en");

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        foreach (var tablePlan in plan.Tables.OrderBy(t => t.Order))
        {
            var inserted = await inserter.InsertTableFromPlanAsync(tablePlan);
            Assert.Equal(RowCount, inserted);
        }

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestTightDecPlan");
        Assert.Equal(RowCount, rows.Count);

        foreach (var row in rows)
        {
            var dec5_2 = (decimal)row["ColDec5_2"]!;
            Assert.InRange(dec5_2, -999.99m, 999.99m);

            var num4_1 = (decimal)row["ColNum4_1"]!;
            Assert.InRange(num4_1, -999.9m, 999.9m);

            Assert.IsType<decimal>(row["ColSmMoney"]);

            var price = (decimal)row["price"]!;
            Assert.InRange(price, -9999.99m, 9999.99m);
        }
    }

    // ══════════════════════════════════════════════
    // 27. SQL_VARIANT Type
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test27_SqlVariantType()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestSqlVariant (
                Id         INT IDENTITY(1,1) PRIMARY KEY,
                ColVariant SQL_VARIANT NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestSqlVariant");
        Assert.Equal(RowCount, results["dbo.TestSqlVariant"]);

        var rows = await _fixture.ExecuteQueryAsync(
            "SELECT SQL_VARIANT_PROPERTY(ColVariant, 'BaseType') AS BaseType, ColVariant FROM dbo.TestSqlVariant");
        Assert.Equal(RowCount, rows.Count);

        var allowedBaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "int", "nvarchar", "varchar", "datetime", "float", "decimal", "numeric"
        };

        foreach (var row in rows)
        {
            var baseType = Assert.IsType<string>(row["BaseType"]);
            Assert.Contains(baseType, allowedBaseTypes);
            Assert.NotNull(row["ColVariant"]);
        }
    }

    // ══════════════════════════════════════════════
    // 28. Unsupported Types (geography, geometry, hierarchyid) Skipped
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test28_UnsupportedTypesSkippedWhenNullable()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestUnsupportedSkip (
                Id         INT IDENTITY(1,1) PRIMARY KEY,
                ColName    NVARCHAR(100)   NOT NULL,
                ColGeo     GEOGRAPHY       NULL,
                ColGeom    GEOMETRY         NULL,
                ColHier    HIERARCHYID      NULL
            )
            """);

        var results = await GenerateDataAsync("TestUnsupportedSkip");
        Assert.Equal(RowCount, results["dbo.TestUnsupportedSkip"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT ColName FROM dbo.TestUnsupportedSkip");
        Assert.Equal(RowCount, rows.Count);
        foreach (var row in rows)
            Assert.IsType<string>(row["ColName"]);

        var allNull = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*) FROM dbo.TestUnsupportedSkip
            WHERE ColGeo IS NULL AND ColGeom IS NULL AND ColHier IS NULL
            """))!;
        Assert.Equal(RowCount, allNull);
    }

    // ══════════════════════════════════════════════
    // 29. Unsupported Non-Nullable Type With DEFAULT
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test29_UnsupportedTypeNonNullableWithDefault()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestUnsupportedDefault (
                Id         INT IDENTITY(1,1) PRIMARY KEY,
                ColName    NVARCHAR(100)                NOT NULL,
                ColGeo     GEOGRAPHY DEFAULT geography::Point(0, 0, 4326) NOT NULL
            )
            """);

        var results = await GenerateDataAsync("TestUnsupportedDefault");
        Assert.Equal(RowCount, results["dbo.TestUnsupportedDefault"]);

        var rows = await _fixture.ExecuteQueryAsync(
            "SELECT ColName, ColGeo.STAsText() AS ColGeoText FROM dbo.TestUnsupportedDefault");
        Assert.Equal(RowCount, rows.Count);

        foreach (var row in rows)
        {
            Assert.IsType<string>(row["ColName"]);
            var geoText = Assert.IsType<string>(row["ColGeoText"]);
            Assert.Contains("POINT", geoText, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ══════════════════════════════════════════════
    // 30. SQL_VARIANT via Plan Execution
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test30_SqlVariantViaPlan()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestSqlVariantPlan (
                Id         INT IDENTITY(1,1) PRIMARY KEY,
                ColVariant SQL_VARIANT NOT NULL
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestSqlVariantPlan").ToList();

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, RowCount, Seed, "en");

        var variantCol = plan.Tables[0].Columns.First(c =>
            c.Name.Equals("ColVariant", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Random.SqlVariant", variantCol.Generator);

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        foreach (var tablePlan in plan.Tables.OrderBy(t => t.Order))
        {
            var inserted = await inserter.InsertTableFromPlanAsync(tablePlan);
            Assert.Equal(RowCount, inserted);
        }

        var rows = await _fixture.ExecuteQueryAsync(
            "SELECT SQL_VARIANT_PROPERTY(ColVariant, 'BaseType') AS BaseType FROM dbo.TestSqlVariantPlan");
        Assert.Equal(RowCount, rows.Count);

        foreach (var row in rows)
        {
            Assert.IsType<string>(row["BaseType"]);
        }
    }

    // ══════════════════════════════════════════════
    // 31. DEFAULT Constraint — Scalar Types via Skip
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test31_DefaultConstraintScalarTypes()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestDefaultScalar (
                Id         INT IDENTITY(1,1) PRIMARY KEY,
                ColInt     INT            NOT NULL DEFAULT 0,
                ColDate    DATETIME2      NOT NULL DEFAULT GETDATE(),
                ColStr     NVARCHAR(50)   NOT NULL DEFAULT 'Pending',
                ColGuid    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID()
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestDefaultScalar").ToList();

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, RowCount, Seed, "en");

        var tablePlan = plan.Tables[0];

        // Verify HasDefault is set on all default columns
        foreach (var colName in new[] { "ColInt", "ColDate", "ColStr", "ColGuid" })
        {
            var col = tablePlan.Columns.First(c =>
                c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
            Assert.True(col.HasDefault, $"{colName} should have HasDefault=true");
        }

        // Set all default columns to skip so the DB fills them
        foreach (var col in tablePlan.Columns)
        {
            if (col.HasDefault)
            {
                col.Generator = "skip";
                col.GeneratorArgs = new Dictionary<string, object?>();
            }
        }

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        var inserted = await inserter.InsertTableFromPlanAsync(tablePlan);
        Assert.Equal(RowCount, inserted);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestDefaultScalar");
        Assert.Equal(RowCount, rows.Count);

        foreach (var row in rows)
        {
            Assert.Equal(0, (int)row["ColInt"]!);
            Assert.IsType<DateTime>(row["ColDate"]);
            Assert.Equal("Pending", (string)row["ColStr"]!);
            Assert.IsType<Guid>(row["ColGuid"]);
        }
    }

    // ══════════════════════════════════════════════
    // 32. DEFAULT Constraint — Explicit Values Override Defaults
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test32_DefaultConstraintInteractionWithExplicitValues()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestDefaultOverride (
                Id         INT IDENTITY(1,1) PRIMARY KEY,
                ColInt     INT            NOT NULL DEFAULT 0,
                ColStr     NVARCHAR(50)   NOT NULL DEFAULT 'Pending',
                ColDate    DATETIME2      NOT NULL DEFAULT '2000-01-01'
            )
            """);

        var results = await GenerateDataAsync("TestDefaultOverride");
        Assert.Equal(RowCount, results["dbo.TestDefaultOverride"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT * FROM dbo.TestDefaultOverride");
        Assert.Equal(RowCount, rows.Count);

        foreach (var row in rows)
        {
            Assert.IsType<int>(row["ColInt"]);
            Assert.IsType<string>(row["ColStr"]);
            Assert.IsType<DateTime>(row["ColDate"]);
        }

        // With generated values, not all rows should have the default value
        var defaultIntCount = rows.Count(r => (int)r["ColInt"]! == 0);
        var defaultStrCount = rows.Count(r => (string)r["ColStr"]! == "Pending");
        Assert.True(defaultIntCount < RowCount,
            "Expected generated values to override defaults, but all ColInt values were 0");
        Assert.True(defaultStrCount < RowCount,
            "Expected generated values to override defaults, but all ColStr values were 'Pending'");
    }

    // ══════════════════════════════════════════════
    // 33. CHECK Constraint — Range (INT)
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test33_CheckConstraintRangeInt()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestCheckRange (
                Id      INT IDENTITY(1,1) PRIMARY KEY,
                Rating  INT NOT NULL,
                CONSTRAINT CK_Rating CHECK (Rating BETWEEN 1 AND 5)
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestCheckRange").ToList();

        // Verify CHECK constraint is read
        Assert.Single(tables[0].CheckConstraints);
        Assert.Equal("CK_Rating", tables[0].CheckConstraints[0].Name);
        Assert.Contains("Rating", tables[0].CheckConstraints[0].Definition);

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, RowCount, Seed, "en");

        // Configure PickRandom generator for valid values
        var ratingCol = plan.Tables[0].Columns.First(c =>
            c.Name.Equals("Rating", StringComparison.OrdinalIgnoreCase));
        ratingCol.Generator = "PickRandom";
        ratingCol.GeneratorArgs = new Dictionary<string, object?>
        {
            ["values"] = new[] { "1", "2", "3", "4", "5" }
        };

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        var inserted = await inserter.InsertTableFromPlanAsync(plan.Tables[0]);
        Assert.Equal(RowCount, inserted);

        var rows = await _fixture.ExecuteQueryAsync("SELECT Rating FROM dbo.TestCheckRange");
        Assert.Equal(RowCount, rows.Count);

        foreach (var row in rows)
        {
            var rating = (int)row["Rating"]!;
            Assert.InRange(rating, 1, 5);
        }
    }

    // ══════════════════════════════════════════════
    // 34. CHECK Constraint — String Enum
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test34_CheckConstraintStringEnum()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestCheckEnum (
                Id      INT IDENTITY(1,1) PRIMARY KEY,
                Status  NVARCHAR(20) NOT NULL,
                CONSTRAINT CK_Status CHECK (Status IN ('Active', 'Inactive', 'Suspended'))
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestCheckEnum").ToList();

        Assert.Single(tables[0].CheckConstraints);

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, RowCount, Seed, "en");

        var statusCol = plan.Tables[0].Columns.First(c =>
            c.Name.Equals("Status", StringComparison.OrdinalIgnoreCase));
        statusCol.Generator = "PickRandom";
        statusCol.GeneratorArgs = new Dictionary<string, object?>
        {
            ["values"] = new[] { "Active", "Inactive", "Suspended" }
        };

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        var inserted = await inserter.InsertTableFromPlanAsync(plan.Tables[0]);
        Assert.Equal(RowCount, inserted);

        var validStatuses = new HashSet<string> { "Active", "Inactive", "Suspended" };
        var rows = await _fixture.ExecuteQueryAsync("SELECT Status FROM dbo.TestCheckEnum");
        foreach (var row in rows)
        {
            var status = (string)row["Status"]!;
            Assert.Contains(status, validStatuses);
        }
    }

    // ══════════════════════════════════════════════
    // 35. CHECK Constraint — Column Comparison
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test35_CheckConstraintColumnComparison()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestCheckDateRange (
                Id        INT IDENTITY(1,1) PRIMARY KEY,
                StartDate DATE NOT NULL,
                EndDate   DATE NOT NULL,
                CONSTRAINT CK_DateRange CHECK (EndDate > StartDate)
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestCheckDateRange").ToList();

        Assert.Single(tables[0].CheckConstraints);
        Assert.Contains("EndDate", tables[0].CheckConstraints[0].Definition);
        Assert.Contains("StartDate", tables[0].CheckConstraints[0].Definition);

        // Insert valid rows via direct SQL to test CHECK is readable and enforceable
        for (var i = 0; i < RowCount; i++)
        {
            var start = new DateTime(2020, 1, 1).AddDays(i * 30);
            var end = start.AddDays(365);
            await _fixture.ExecuteSqlAsync(
                $"INSERT INTO dbo.TestCheckDateRange (StartDate, EndDate) VALUES ('{start:yyyy-MM-dd}', '{end:yyyy-MM-dd}')");
        }

        var rows = await _fixture.ExecuteQueryAsync(
            "SELECT StartDate, EndDate FROM dbo.TestCheckDateRange");
        Assert.Equal(RowCount, rows.Count);

        foreach (var row in rows)
        {
            var start = (DateTime)row["StartDate"]!;
            var end = (DateTime)row["EndDate"]!;
            Assert.True(end > start, $"EndDate {end} should be > StartDate {start}");
        }

        // Verify the CHECK constraint actually rejects invalid data
        var violated = false;
        try
        {
            await _fixture.ExecuteSqlAsync(
                "INSERT INTO dbo.TestCheckDateRange (StartDate, EndDate) VALUES ('2025-01-01', '2020-01-01')");
        }
        catch
        {
            violated = true;
        }
        Assert.True(violated, "CHECK constraint should reject EndDate <= StartDate");
    }

    // ══════════════════════════════════════════════
    // 36. UNIQUE Constraint — Single Column
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test36_UniqueConstraintSingleColumn()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestUniqueSingle (
                Id    INT IDENTITY(1,1) PRIMARY KEY,
                Email NVARCHAR(200) NOT NULL,
                Label NVARCHAR(50) NOT NULL,
                CONSTRAINT UQ_Email UNIQUE (Email)
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var table = allTables.First(t => t.TableName == "TestUniqueSingle");

        // Verify unique constraint was read
        Assert.Single(table.UniqueConstraints);
        Assert.Equal("UQ_Email", table.UniqueConstraints[0].Name);
        Assert.Single(table.UniqueConstraints[0].Columns);
        Assert.Equal("Email", table.UniqueConstraints[0].Columns[0]);

        // Verify column flag
        var emailCol = table.Columns.First(c =>
            c.Name.Equals("Email", StringComparison.OrdinalIgnoreCase));
        Assert.True(emailCol.IsUnique);

        var graph = new DependencyGraph();
        graph.Build([table]);
        var sorted = graph.GetTopologicalOrder();

        var valueGen = new ColumnValueGenerator(seed: Seed);
        var inserter = new DataInserter(
            _fixture.ConnectionString, valueGen, graph.SelfReferencingTables);

        var inserted = await inserter.InsertTableAsync(table, 50);
        Assert.Equal(50, inserted);

        var distinctEmails = (int)(await _fixture.ExecuteScalarAsync(
            "SELECT COUNT(DISTINCT Email) FROM dbo.TestUniqueSingle"))!;
        Assert.Equal(50, distinctEmails);
    }

    // ══════════════════════════════════════════════
    // 37. UNIQUE Constraint — Multi-Column
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test37_UniqueConstraintMultiColumn()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestUniqueMulti (
                Id        INT IDENTITY(1,1) PRIMARY KEY,
                FirstName NVARCHAR(100) NOT NULL,
                LastName  NVARCHAR(100) NOT NULL,
                Age       INT NOT NULL,
                CONSTRAINT UQ_FullName UNIQUE (FirstName, LastName)
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var table = allTables.First(t => t.TableName == "TestUniqueMulti");

        Assert.Single(table.UniqueConstraints);
        Assert.Equal(2, table.UniqueConstraints[0].Columns.Count);

        var graph = new DependencyGraph();
        graph.Build([table]);
        var sorted = graph.GetTopologicalOrder();

        var valueGen = new ColumnValueGenerator(seed: Seed);
        var inserter = new DataInserter(
            _fixture.ConnectionString, valueGen, graph.SelfReferencingTables);

        var inserted = await inserter.InsertTableAsync(table, 50);
        Assert.Equal(50, inserted);

        var distinctPairs = (int)(await _fixture.ExecuteScalarAsync(
            "SELECT COUNT(DISTINCT CONCAT(FirstName, '|', LastName)) FROM dbo.TestUniqueMulti"))!;
        Assert.Equal(50, distinctPairs);
    }

    // ══════════════════════════════════════════════
    // 38. UNIQUE Constraint — Nullable Column
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test38_UniqueConstraintWithNullable()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestUniqueNullable (
                Id           INT IDENTITY(1,1) PRIMARY KEY,
                NullableCode NVARCHAR(10) NULL,
                Label        NVARCHAR(50) NOT NULL,
                CONSTRAINT UQ_NullableCode UNIQUE (NullableCode)
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var table = allTables.First(t => t.TableName == "TestUniqueNullable");

        Assert.Single(table.UniqueConstraints);

        var graph = new DependencyGraph();
        graph.Build([table]);
        var sorted = graph.GetTopologicalOrder();

        var valueGen = new ColumnValueGenerator(seed: Seed);
        var inserter = new DataInserter(
            _fixture.ConnectionString, valueGen, graph.SelfReferencingTables);

        var inserted = await inserter.InsertTableAsync(table, RowCount);
        Assert.Equal(RowCount, inserted);

        // Non-NULL values must be distinct
        var nonNullDistinct = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(DISTINCT NullableCode)
            FROM dbo.TestUniqueNullable
            WHERE NullableCode IS NOT NULL
            """))!;
        var nonNullTotal = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*)
            FROM dbo.TestUniqueNullable
            WHERE NullableCode IS NOT NULL
            """))!;
        Assert.Equal(nonNullTotal, nonNullDistinct);
    }

    // ══════════════════════════════════════════════
    // 39. UNIQUE Constraint via Plan Execution
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test39_UniqueConstraintViaPlan()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestUniquePlan (
                Id    INT IDENTITY(1,1) PRIMARY KEY,
                Email NVARCHAR(200) NOT NULL,
                Label NVARCHAR(50) NOT NULL,
                CONSTRAINT UQ_PlanEmail UNIQUE (Email)
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestUniquePlan").ToList();

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 50, Seed, "en");

        // Verify IsUnique propagated to plan
        var emailCol = plan.Tables[0].Columns.First(c =>
            c.Name.Equals("Email", StringComparison.OrdinalIgnoreCase));
        Assert.True(emailCol.IsUnique);

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        var inserted = await inserter.InsertTableFromPlanAsync(plan.Tables[0]);
        Assert.Equal(50, inserted);

        var distinctEmails = (int)(await _fixture.ExecuteScalarAsync(
            "SELECT COUNT(DISTINCT Email) FROM dbo.TestUniquePlan"))!;
        Assert.Equal(50, distinctEmails);
    }

    // ══════════════════════════════════════════════
    // 40. CHECK Constraint Violation — Error Message
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test40_CheckConstraintViolationErrorMessage()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestCheckViolation (
                Id   INT IDENTITY(1,1) PRIMARY KEY,
                Age  INT NOT NULL,
                CONSTRAINT CK_Age CHECK (Age >= 0 AND Age <= 150)
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestCheckViolation").ToList();

        Assert.Single(tables[0].CheckConstraints);
        Assert.Equal("CK_Age", tables[0].CheckConstraints[0].Name);

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 1, Seed, "en");

        // Force a value that violates the CHECK constraint
        var ageCol = plan.Tables[0].Columns.First(c =>
            c.Name.Equals("Age", StringComparison.OrdinalIgnoreCase));
        ageCol.Generator = "PickRandom";
        ageCol.GeneratorArgs = new Dictionary<string, object?>
        {
            ["values"] = new[] { "999" }
        };

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        var ex = await Assert.ThrowsAsync<DataGenerationException>(
            () => inserter.InsertTableFromPlanAsync(plan.Tables[0]));

        Assert.Contains("CK_Age", ex.Message);
        Assert.Contains("CHECK constraint", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════
    // 41. Filtered Unique Index — IS NOT NULL
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test41_FilteredUniqueIndex_IsNotNull()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestFilteredUniqueNotNull (
                Id    INT IDENTITY(1,1) PRIMARY KEY,
                Email NVARCHAR(200) NULL,
                Label NVARCHAR(50) NOT NULL
            );
            CREATE UNIQUE INDEX UX_Email_NotNull
                ON dbo.TestFilteredUniqueNotNull (Email)
                WHERE Email IS NOT NULL;
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var table = allTables.First(t => t.TableName == "TestFilteredUniqueNotNull");

        Assert.Single(table.UniqueConstraints);
        Assert.Equal("UX_Email_NotNull", table.UniqueConstraints[0].Name);
        Assert.NotNull(table.UniqueConstraints[0].FilterDefinition);
        Assert.Contains("IS NOT NULL", table.UniqueConstraints[0].FilterDefinition!,
            StringComparison.OrdinalIgnoreCase);

        var graph = new DependencyGraph();
        graph.Build([table]);

        var valueGen = new ColumnValueGenerator(seed: Seed);
        var inserter = new DataInserter(
            _fixture.ConnectionString, valueGen, graph.SelfReferencingTables);

        var inserted = await inserter.InsertTableAsync(table, 50);
        Assert.Equal(50, inserted);

        // Non-null emails must be distinct
        var nonNullDistinct = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(DISTINCT Email)
            FROM dbo.TestFilteredUniqueNotNull
            WHERE Email IS NOT NULL
            """))!;
        var nonNullTotal = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*)
            FROM dbo.TestFilteredUniqueNotNull
            WHERE Email IS NOT NULL
            """))!;
        Assert.Equal(nonNullTotal, nonNullDistinct);
    }

    // ══════════════════════════════════════════════
    // 42. Filtered Unique Index — Equality Predicate
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test42_FilteredUniqueIndex_EqualityPredicate()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestFilteredUniqueEquality (
                Id     INT IDENTITY(1,1) PRIMARY KEY,
                Code   NVARCHAR(50) NOT NULL,
                Status NVARCHAR(20) NOT NULL DEFAULT 'Active'
            );
            CREATE UNIQUE INDEX UX_Code_Active
                ON dbo.TestFilteredUniqueEquality (Code)
                WHERE Status = 'Active';
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var table = allTables.First(t => t.TableName == "TestFilteredUniqueEquality");

        Assert.Single(table.UniqueConstraints);
        Assert.NotNull(table.UniqueConstraints[0].FilterDefinition);

        var graph = new DependencyGraph();
        graph.Build([table]);

        var valueGen = new ColumnValueGenerator(seed: Seed);
        var inserter = new DataInserter(
            _fixture.ConnectionString, valueGen, graph.SelfReferencingTables);

        var inserted = await inserter.InsertTableAsync(table, 50);
        Assert.Equal(50, inserted);

        // Among Active rows, Code must be distinct
        var activeDistinct = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(DISTINCT Code)
            FROM dbo.TestFilteredUniqueEquality
            WHERE Status = 'Active'
            """))!;
        var activeTotal = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*)
            FROM dbo.TestFilteredUniqueEquality
            WHERE Status = 'Active'
            """))!;
        Assert.Equal(activeTotal, activeDistinct);
    }

    // ══════════════════════════════════════════════
    // 43. Filtered Unique Index — Schema Reading
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test43_FilteredUniqueIndex_SchemaReading()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestFilteredSchemaRead (
                Id    INT IDENTITY(1,1) PRIMARY KEY,
                Code  NVARCHAR(50) NOT NULL,
                Email NVARCHAR(200) NOT NULL,
                CONSTRAINT UQ_Code_Unfiltered UNIQUE (Code)
            );
            CREATE UNIQUE INDEX UX_Email_Filtered
                ON dbo.TestFilteredSchemaRead (Email)
                WHERE Email IS NOT NULL;
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var table = allTables.First(t => t.TableName == "TestFilteredSchemaRead");

        Assert.Equal(2, table.UniqueConstraints.Count);

        var unfiltered = table.UniqueConstraints.First(uc => uc.Name == "UQ_Code_Unfiltered");
        Assert.Null(unfiltered.FilterDefinition);
        Assert.Single(unfiltered.Columns);
        Assert.Equal("Code", unfiltered.Columns[0]);

        var filtered = table.UniqueConstraints.First(uc => uc.Name == "UX_Email_Filtered");
        Assert.NotNull(filtered.FilterDefinition);
        Assert.Contains("IS NOT NULL", filtered.FilterDefinition!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(filtered.Columns);
        Assert.Equal("Email", filtered.Columns[0]);
    }

    // ══════════════════════════════════════════════
    // 44. Filtered Unique Index — Plan Path
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test44_FilteredUniqueIndex_ViaPlan()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestFilteredUniquePlan (
                Id    INT IDENTITY(1,1) PRIMARY KEY,
                Email NVARCHAR(200) NULL,
                Label NVARCHAR(50) NOT NULL
            );
            CREATE UNIQUE INDEX UX_PlanEmail_NotNull
                ON dbo.TestFilteredUniquePlan (Email)
                WHERE Email IS NOT NULL;
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestFilteredUniquePlan").ToList();

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, 50, Seed, "en");

        // Verify UniqueConstraints propagated to plan with filter
        Assert.NotNull(plan.Tables[0].UniqueConstraints);
        Assert.Single(plan.Tables[0].UniqueConstraints!);
        Assert.Equal("UX_PlanEmail_NotNull", plan.Tables[0].UniqueConstraints![0].Name);
        Assert.NotNull(plan.Tables[0].UniqueConstraints![0].FilterDefinition);

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        var inserted = await inserter.InsertTableFromPlanAsync(plan.Tables[0]);
        Assert.Equal(50, inserted);

        // Non-null emails must be distinct
        var nonNullDistinct = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(DISTINCT Email)
            FROM dbo.TestFilteredUniquePlan
            WHERE Email IS NOT NULL
            """))!;
        var nonNullTotal = (int)(await _fixture.ExecuteScalarAsync("""
            SELECT COUNT(*)
            FROM dbo.TestFilteredUniquePlan
            WHERE Email IS NOT NULL
            """))!;
        Assert.Equal(nonNullTotal, nonNullDistinct);
    }

    // ══════════════════════════════════════════════
    // 45. Sequence Primary Key (Direct Path)
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test45_SequencePrimaryKey()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE SEQUENCE dbo.SeqTestPK_Seq AS INT START WITH 1 INCREMENT BY 1;

            CREATE TABLE dbo.TestSeqPK (
                Id   INT NOT NULL DEFAULT (NEXT VALUE FOR dbo.SeqTestPK_Seq) PRIMARY KEY,
                Name NVARCHAR(100) NOT NULL
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestSeqPK").ToList();

        Assert.Single(tables);
        var idCol = tables[0].Columns.First(c => c.Name == "Id");
        Assert.True(idCol.IsSequenceDefault, "Id column should be detected as sequence default");
        Assert.False(idCol.IsIdentity, "Id column should not be identity");
        Assert.True(tables[0].HasSequencePk, "Table should have HasSequencePk = true");

        var results = await GenerateDataAsync("TestSeqPK");
        Assert.Equal(RowCount, results["dbo.TestSeqPK"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT Id, Name FROM dbo.TestSeqPK ORDER BY Id");
        Assert.Equal(RowCount, rows.Count);

        // PK values should be sequential integers assigned by the sequence
        var ids = rows.Select(r => (int)r["Id"]!).ToList();
        for (var i = 1; i < ids.Count; i++)
            Assert.Equal(ids[i - 1] + 1, ids[i]);

        // Name column should have non-null values (generator filled them)
        foreach (var row in rows)
            Assert.False(string.IsNullOrWhiteSpace((string?)row["Name"]));
    }

    // ══════════════════════════════════════════════
    // 46. Sequence Non-PK Column (Direct Path)
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test46_SequenceNonPkColumn()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE SEQUENCE dbo.SeqTestNonPK_Seq AS INT START WITH 100 INCREMENT BY 10;

            CREATE TABLE dbo.TestSeqNonPK (
                Id     INT IDENTITY(1,1) PRIMARY KEY,
                SeqNum INT NOT NULL DEFAULT (NEXT VALUE FOR dbo.SeqTestNonPK_Seq),
                Label  NVARCHAR(50) NOT NULL
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestSeqNonPK").ToList();

        Assert.Single(tables);
        var seqCol = tables[0].Columns.First(c => c.Name == "SeqNum");
        Assert.True(seqCol.IsSequenceDefault, "SeqNum should be detected as sequence default");

        var results = await GenerateDataAsync("TestSeqNonPK");
        Assert.Equal(RowCount, results["dbo.TestSeqNonPK"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT SeqNum FROM dbo.TestSeqNonPK ORDER BY Id");
        Assert.Equal(RowCount, rows.Count);

        // SeqNum values should be filled by the sequence (100, 110, 120, ...)
        var seqNums = rows.Select(r => (int)r["SeqNum"]!).ToList();
        Assert.Equal(100, seqNums[0]);
        for (var i = 1; i < seqNums.Count; i++)
            Assert.Equal(seqNums[i - 1] + 10, seqNums[i]);
    }

    // ══════════════════════════════════════════════
    // 47. Sequence Primary Key via Plan Execution
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test47_SequencePrimaryKey_ViaPlan()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE SEQUENCE dbo.SeqTestPlan_Seq AS INT START WITH 1 INCREMENT BY 1;

            CREATE TABLE dbo.TestSeqPKPlan (
                Id   INT NOT NULL DEFAULT (NEXT VALUE FOR dbo.SeqTestPlan_Seq) PRIMARY KEY,
                Name NVARCHAR(100) NOT NULL
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestSeqPKPlan").ToList();

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, RowCount, Seed, "en");

        var tablePlan = plan.Tables[0];

        // Verify the plan correctly marks the sequence column
        var idPlan = tablePlan.Columns.First(c => c.Name == "Id");
        Assert.True(idPlan.IsSequenceDefault, "Plan should have IsSequenceDefault = true for Id");
        Assert.Equal("skip", idPlan.Generator);

        var namePlan = tablePlan.Columns.First(c => c.Name == "Name");
        Assert.NotEqual("skip", namePlan.Generator);

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        var inserted = await inserter.InsertTableFromPlanAsync(tablePlan);
        Assert.Equal(RowCount, inserted);

        var rows = await _fixture.ExecuteQueryAsync("SELECT Id, Name FROM dbo.TestSeqPKPlan ORDER BY Id");
        Assert.Equal(RowCount, rows.Count);

        // PK values should be sequential integers assigned by the sequence
        var ids = rows.Select(r => (int)r["Id"]!).ToList();
        for (var i = 1; i < ids.Count; i++)
            Assert.Equal(ids[i - 1] + 1, ids[i]);
    }

    // ══════════════════════════════════════════════
    // 48. RowVersion Column (Direct Path)
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test48_RowVersionColumn()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestRowVersion (
                Id     INT IDENTITY(1,1) PRIMARY KEY,
                Name   NVARCHAR(50) NOT NULL,
                RowVer ROWVERSION
            )
            """);

        var results = await GenerateDataAsync("TestRowVersion");
        Assert.Equal(RowCount, results["dbo.TestRowVersion"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT Id, Name, RowVer FROM dbo.TestRowVersion ORDER BY Id");
        Assert.Equal(RowCount, rows.Count);

        var rowVersions = new HashSet<string>();
        foreach (var row in rows)
        {
            Assert.NotNull(row["RowVer"]);
            var hex = Convert.ToHexString((byte[])row["RowVer"]!);
            Assert.True(rowVersions.Add(hex), "Each row should have a distinct rowversion value");
        }
    }

    // ══════════════════════════════════════════════
    // 49. Timestamp Column (Legacy Alias, Direct Path)
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test49_TimestampColumn()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestTimestamp (
                Id    INT IDENTITY(1,1) PRIMARY KEY,
                Name  NVARCHAR(50) NOT NULL,
                Stamp TIMESTAMP
            )
            """);

        var results = await GenerateDataAsync("TestTimestamp");
        Assert.Equal(RowCount, results["dbo.TestTimestamp"]);

        var rows = await _fixture.ExecuteQueryAsync("SELECT Id, Name, Stamp FROM dbo.TestTimestamp ORDER BY Id");
        Assert.Equal(RowCount, rows.Count);

        var timestamps = new HashSet<string>();
        foreach (var row in rows)
        {
            Assert.NotNull(row["Stamp"]);
            var hex = Convert.ToHexString((byte[])row["Stamp"]!);
            Assert.True(timestamps.Add(hex), "Each row should have a distinct timestamp value");
        }
    }

    // ══════════════════════════════════════════════
    // 50. RowVersion Column via Plan Execution
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test50_RowVersionColumn_ViaPlan()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestRowVersionPlan (
                Id     INT IDENTITY(1,1) PRIMARY KEY,
                Name   NVARCHAR(50) NOT NULL,
                RowVer ROWVERSION
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestRowVersionPlan").ToList();

        Assert.Single(tables);
        var rvCol = tables[0].Columns.First(c => c.Name == "RowVer");
        Assert.True(rvCol.IsRowVersion, "RowVer column should be detected as rowversion");

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, RowCount, Seed, "en");

        var tablePlan = plan.Tables[0];

        var rvPlan = tablePlan.Columns.First(c => c.Name == "RowVer");
        Assert.True(rvPlan.IsRowVersion, "Plan should have IsRowVersion = true for RowVer");
        Assert.Equal("skip", rvPlan.Generator);

        var namePlan = tablePlan.Columns.First(c => c.Name == "Name");
        Assert.NotEqual("skip", namePlan.Generator);

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        var inserted = await inserter.InsertTableFromPlanAsync(tablePlan);
        Assert.Equal(RowCount, inserted);

        var rows = await _fixture.ExecuteQueryAsync("SELECT RowVer FROM dbo.TestRowVersionPlan");
        Assert.Equal(RowCount, rows.Count);

        var rowVersions = new HashSet<string>();
        foreach (var row in rows)
        {
            Assert.NotNull(row["RowVer"]);
            var hex = Convert.ToHexString((byte[])row["RowVer"]!);
            Assert.True(rowVersions.Add(hex), "Each row should have a distinct rowversion value");
        }
    }

    // ══════════════════════════════════════════════
    // 51. Timestamp Column (Legacy Alias) via Plan Execution
    // ══════════════════════════════════════════════

    [Fact]
    public async Task Test51_TimestampColumn_ViaPlan()
    {
        await _fixture.ExecuteSqlAsync("""
            CREATE TABLE dbo.TestTimestampPlan (
                Id    INT IDENTITY(1,1) PRIMARY KEY,
                Name  NVARCHAR(50) NOT NULL,
                Stamp TIMESTAMP
            )
            """);

        var reader = new SchemaReader(_fixture.ConnectionString);
        var allTables = await reader.ReadSchemaAsync();
        var tables = allTables.Where(t => t.TableName == "TestTimestampPlan").ToList();

        Assert.Single(tables);
        var tsCol = tables[0].Columns.First(c => c.Name == "Stamp");
        Assert.True(tsCol.IsRowVersion, "Stamp column (TIMESTAMP) should be detected as rowversion");

        var graph = new DependencyGraph();
        graph.Build(tables);
        var sorted = graph.GetTopologicalOrder();

        var planGen = new PlanGenerator();
        var plan = planGen.Generate(sorted, graph.SelfReferencingTables, RowCount, Seed, "en");

        var tablePlan = plan.Tables[0];

        var tsPlan = tablePlan.Columns.First(c => c.Name == "Stamp");
        Assert.True(tsPlan.IsRowVersion, "Plan should have IsRowVersion = true for Stamp");
        Assert.Equal("skip", tsPlan.Generator);

        var namePlan = tablePlan.Columns.First(c => c.Name == "Name");
        Assert.NotEqual("skip", namePlan.Generator);

        var valueGen = new ColumnValueGenerator(plan.Seed, plan.Locale);
        var inserter = new DataInserter(_fixture.ConnectionString, valueGen, new HashSet<string>());

        var inserted = await inserter.InsertTableFromPlanAsync(tablePlan);
        Assert.Equal(RowCount, inserted);

        var rows = await _fixture.ExecuteQueryAsync("SELECT Stamp FROM dbo.TestTimestampPlan");
        Assert.Equal(RowCount, rows.Count);

        var timestamps = new HashSet<string>();
        foreach (var row in rows)
        {
            Assert.NotNull(row["Stamp"]);
            var hex = Convert.ToHexString((byte[])row["Stamp"]!);
            Assert.True(timestamps.Add(hex), "Each row should have a distinct timestamp value");
        }
    }
}
