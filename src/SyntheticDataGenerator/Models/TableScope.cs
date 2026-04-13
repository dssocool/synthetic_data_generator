using Microsoft.Extensions.Configuration;

namespace SyntheticDataGenerator.Models;

public class TableScope
{
    public string Table { get; set; } = string.Empty;
    public List<string>? Columns { get; set; }
}

public class ScopeConfig
{
    public string? SchemaFilter { get; }
    public TableScope[] TablesToInclude { get; }
    public string[] TablesToExclude { get; }
    public int RowsPerTable { get; }
    public int? Seed { get; }
    public string Locale { get; }

    public ScopeConfig(
        string? schemaFilter,
        TableScope[] tablesToInclude,
        string[] tablesToExclude,
        int rowsPerTable,
        int? seed,
        string locale)
    {
        SchemaFilter = schemaFilter;
        TablesToInclude = tablesToInclude;
        TablesToExclude = tablesToExclude;
        RowsPerTable = rowsPerTable;
        Seed = seed;
        Locale = locale;
    }

    /// <summary>
    /// Builds the per-table column scope dictionary from TablesToInclude entries that
    /// specify explicit column lists. Returns null if no table has column restrictions.
    /// </summary>
    public Dictionary<string, HashSet<string>>? BuildColumnScope()
    {
        Dictionary<string, HashSet<string>>? scope = null;

        foreach (var entry in TablesToInclude)
        {
            if (entry.Columns is { Count: > 0 })
            {
                scope ??= new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                scope[entry.Table] = new HashSet<string>(entry.Columns, StringComparer.OrdinalIgnoreCase);
            }
        }

        return scope;
    }

    /// <summary>
    /// Parses TablesToInclude from IConfiguration, handling both plain strings
    /// ("- dbo.Users") and structured objects ("- Table: dbo.Users / Columns: [...]").
    /// </summary>
    public static TableScope[] ParseTablesToInclude(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
            return [];

        var result = new List<TableScope>();

        foreach (var child in children)
        {
            var tableValue = child["Table"];
            if (tableValue is not null)
            {
                var columns = child.GetSection("Columns").Get<string[]>();
                result.Add(new TableScope
                {
                    Table = tableValue,
                    Columns = columns?.Length > 0 ? columns.ToList() : null
                });
            }
            else
            {
                var plainValue = child.Value;
                if (!string.IsNullOrWhiteSpace(plainValue))
                {
                    result.Add(new TableScope { Table = plainValue });
                }
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Gets the set of table names from TablesToInclude for filtering.
    /// </summary>
    public HashSet<string> GetIncludeTableNames()
    {
        return new HashSet<string>(
            TablesToInclude.Select(t => t.Table),
            StringComparer.OrdinalIgnoreCase);
    }
}
