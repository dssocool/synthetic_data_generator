namespace SyntheticDataGenerator.Models;

/// <summary>
/// Parses and formats include-scope lines such as
/// <c>MyDb.dbo.Orders</c> or <c>MyDb.dbo.Orders(Id, Email)</c>.
/// </summary>
public readonly record struct IncludeScopePattern(string TablePattern, IReadOnlyList<string>? Columns)
{
    public bool HasColumnSelection => Columns is { Count: > 0 };

    public static IncludeScopePattern Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return new IncludeScopePattern(string.Empty, null);

        var trimmed = line.Trim();
        var openParen = trimmed.LastIndexOf('(');
        if (openParen >= 0 && trimmed.EndsWith(')'))
        {
            var tablePart = trimmed[..openParen].Trim();
            var columnsPart = trimmed[(openParen + 1)..^1];
            var columns = columnsPart
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            return new IncludeScopePattern(
                SqlTableName.NormalizeIdentifier(tablePart),
                columns.Count > 0 ? columns : null);
        }

        return new IncludeScopePattern(SqlTableName.NormalizeIdentifier(trimmed), null);
    }

    public string ToIncludeLine()
    {
        if (!HasColumnSelection)
            return TablePattern;

        return $"{TablePattern}({string.Join(", ", Columns!)})";
    }

    public bool ContainsColumn(string columnName) =>
        Columns is not null
        && Columns.Any(c => string.Equals(c, columnName, StringComparison.OrdinalIgnoreCase));
}
