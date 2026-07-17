namespace SyntheticDataGenerator.Models;

/// <summary>
/// Parses and formats SQL Server three-part identifiers (database.schema.table).
/// When <see cref="Database"/> is empty the name is treated as two-part (schema.table).
/// </summary>
public readonly record struct SqlTableName(string Database, string Schema, string TableName)
{
    public string FullName =>
        string.IsNullOrEmpty(Database)
            ? $"{Schema}.{TableName}"
            : $"{Database}.{Schema}.{TableName}";

    /// <summary>
    /// Bracket-quoted SQL object name: [db].[schema].[table] or [schema].[table].
    /// </summary>
    public string Bracketed =>
        string.IsNullOrEmpty(Database)
            ? $"[{Schema}].[{TableName}]"
            : $"[{Database}].[{Schema}].[{TableName}]";

    /// <summary>
    /// Strips bracket quoting from a pattern or identifier, e.g. [MyDb].[dbo].[Orders] -> MyDb.dbo.Orders.
    /// </summary>
    public static string NormalizeIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        return name.Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>
    /// Formats a scope pattern as bracket-quoted segments: [db], [db].[schema], or [db].[schema].[table].
    /// </summary>
    public static string ToBracketedPattern(string pattern)
    {
        var normalized = NormalizeIdentifier(pattern);
        if (string.IsNullOrWhiteSpace(normalized))
            return pattern;

        var (db, schema, tableName) = ParsePattern(normalized);
        if (string.IsNullOrEmpty(db))
            return pattern;

        if (tableName is not null)
            return $"[{db}].[{schema}].[{tableName}]";

        if (schema is not null)
            return $"[{db}].[{schema}]";

        return $"[{db}]";
    }

    /// <summary>
    /// Parses a table identifier with 1, 2, or 3 dot-separated segments.
    /// One segment -> table only (schema defaults to dbo).
    /// Two segments -> schema.table.
    /// Three or more -> database.schema.table (extra dots stay in table name).
    /// </summary>
    public static SqlTableName Parse(string? name)
    {
        name = NormalizeIdentifier(name);
        if (string.IsNullOrWhiteSpace(name))
            return new SqlTableName(string.Empty, string.Empty, string.Empty);

        var parts = name.Split('.');
        return parts.Length switch
        {
            1 => new SqlTableName(string.Empty, "dbo", parts[0]),
            2 => new SqlTableName(string.Empty, parts[0], parts[1]),
            _ => new SqlTableName(parts[0], parts[1], string.Join('.', parts.Skip(2)))
        };
    }

    /// <summary>
    /// Parses a scope pattern: db, db.schema, or db.schema.table (1–3 segments).
    /// </summary>
    public static (string Database, string? Schema, string? TableName) ParsePattern(string pattern)
    {
        pattern = NormalizeIdentifier(pattern);
        if (string.IsNullOrWhiteSpace(pattern))
            return (string.Empty, null, null);

        var parts = pattern.Split('.');
        return parts.Length switch
        {
            1 => (parts[0], null, null),
            2 => (parts[0], parts[1], null),
            _ => (parts[0], parts[1], string.Join('.', parts.Skip(2)))
        };
    }

    /// <summary>
    /// Returns true when <paramref name="tableFullName"/> matches a scope pattern
    /// (db / db.schema / db.schema.table), case-insensitive.
    /// </summary>
    public static bool MatchesPattern(string tableFullName, string pattern)
    {
        var table = Parse(tableFullName);
        var (db, schema, tableName) = ParsePattern(pattern);

        if (string.IsNullOrEmpty(table.Database))
        {
            // Legacy 2-part names only match 2-part patterns without a database prefix.
            if (!string.IsNullOrEmpty(db) && schema is null)
                return false;
            if (schema is null)
                return string.Equals(table.Schema, db, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(table.TableName, db, StringComparison.OrdinalIgnoreCase);
            if (tableName is null)
                return string.Equals(table.Schema, schema, StringComparison.OrdinalIgnoreCase);
            return string.Equals(table.Schema, schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(table.TableName, tableName, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.Equals(table.Database, db, StringComparison.OrdinalIgnoreCase))
            return false;

        if (schema is null)
            return true;

        if (!string.Equals(table.Schema, schema, StringComparison.OrdinalIgnoreCase))
            return false;

        if (tableName is null)
            return true;

        return string.Equals(table.TableName, tableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the database segment from any pattern or table name (first segment).
    /// </summary>
    public static string ExtractDatabase(string name)
    {
        name = NormalizeIdentifier(name);
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }
}
