using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Tests;

public class IncludeScopePatternTests
{
    [Fact]
    public void Parse_TableOnly_HasNoColumnSelection()
    {
        var parsed = IncludeScopePattern.Parse("MyDb.dbo.Orders");

        Assert.Equal("MyDb.dbo.Orders", parsed.TablePattern);
        Assert.False(parsed.HasColumnSelection);
        Assert.Null(parsed.Columns);
    }

    [Fact]
    public void Parse_TableWithColumns_ParsesColumnList()
    {
        var parsed = IncludeScopePattern.Parse("MyDb.dbo.Orders(Id, Email)");

        Assert.Equal("MyDb.dbo.Orders", parsed.TablePattern);
        Assert.True(parsed.HasColumnSelection);
        Assert.Equal(["Id", "Email"], parsed.Columns);
    }

    [Fact]
    public void Parse_BracketedTableWithColumns_NormalizesTable()
    {
        var parsed = IncludeScopePattern.Parse("[MyDb].[dbo].[Orders](Id, Email)");

        Assert.Equal("MyDb.dbo.Orders", parsed.TablePattern);
        Assert.Equal(["Id", "Email"], parsed.Columns);
    }

    [Fact]
    public void ToIncludeLine_RoundTripsTableAndColumns()
    {
        var original = new IncludeScopePattern("MyDb.dbo.Orders", ["Id", "Email"]);
        var roundTripped = IncludeScopePattern.Parse(original.ToIncludeLine());

        Assert.Equal(original.TablePattern, roundTripped.TablePattern);
        Assert.Equal(original.Columns, roundTripped.Columns);
    }

    [Fact]
    public void ContainsColumn_IsCaseInsensitive()
    {
        var parsed = new IncludeScopePattern("MyDb.dbo.Orders", ["Id", "Email"]);

        Assert.True(parsed.ContainsColumn("id"));
        Assert.True(parsed.ContainsColumn("EMAIL"));
        Assert.False(parsed.ContainsColumn("Name"));
    }
}
