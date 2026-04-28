using Microsoft.Extensions.Configuration;

namespace SyntheticDataGenerator.Models;

public class TableScope
{
    public string Table { get; set; } = string.Empty;
    public List<string>? Columns { get; set; }
}

public class CustomValueList
{
    public string Column { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;

    /// <summary>
    /// Inline list of values. Mutually exclusive with <see cref="File"/>:
    /// exactly one of the two must be provided per entry.
    /// </summary>
    public List<string>? Values { get; set; }
}

public class ScopeConfig
{
    public string[]? SchemaFilter { get; }
    public TableScope[] TablesToInclude { get; }
    public int RowsPerTable { get; }
    public int? Seed { get; }
    public string Locale { get; }
    public string[] CustomDependencies { get; }
    public CustomValueList[] CustomValueLists { get; }

    /// <summary>
    /// Maximum number of values held in memory per external custom-dependency
    /// root column. Streamer pulls one row at a time from the DB to keep this
    /// window rotating across the full result set. Defaults to 10,000.
    /// </summary>
    public int CustomDependencyBufferSize { get; }

    /// <summary>
    /// Maximum number of unrelated tables that may be inserted/updated in
    /// parallel. Defaults to <see cref="Environment.ProcessorCount"/>; set to
    /// 1 to disable parallelism. Tables only run concurrently when they have
    /// no FK or customDependency edge between them.
    /// </summary>
    public int MaxParallelTables { get; }

    public ScopeConfig(
        string[]? schemaFilter,
        TableScope[] tablesToInclude,
        int rowsPerTable,
        int? seed,
        string locale,
        string[]? customDependencies = null,
        int customDependencyBufferSize = 10_000,
        CustomValueList[]? customValueLists = null,
        int? maxParallelTables = null)
    {
        SchemaFilter = schemaFilter is { Length: > 0 } ? schemaFilter : null;
        TablesToInclude = tablesToInclude;
        RowsPerTable = rowsPerTable;
        Seed = seed;
        Locale = locale;
        CustomDependencies = customDependencies ?? [];
        CustomValueLists = customValueLists ?? [];
        CustomDependencyBufferSize = customDependencyBufferSize > 0
            ? customDependencyBufferSize
            : 10_000;
        MaxParallelTables = maxParallelTables is > 0
            ? maxParallelTables.Value
            : Math.Max(1, Environment.ProcessorCount);
    }

    /// <summary>
    /// Parses the Schema config section, supporting both a single string and a list of strings.
    /// </summary>
    public static string[]? ParseSchemaFilter(IConfigurationSection section)
    {
        var singleValue = section.Value;
        if (!string.IsNullOrWhiteSpace(singleValue))
            return [singleValue];

        var list = section.GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToArray();

        return list.Length > 0 ? list : null;
    }

    /// <summary>
    /// Parses CustomDependencies strings into structured groups.
    /// Each string is "schema.table.col|schema.table2.col2|..." where the first entry is the source.
    /// </summary>
    public static List<CustomDependencyGroup> ParseCustomDependencies(string[] raw)
    {
        var groups = new List<CustomDependencyGroup>();
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var refs = new List<CustomColumnRef>();
            foreach (var part in entry.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var lastDot = part.LastIndexOf('.');
                if (lastDot <= 0)
                    continue;
                var table = part[..lastDot];
                var column = part[(lastDot + 1)..];
                refs.Add(new CustomColumnRef { Table = table, Column = column });
            }

            if (refs.Count >= 2)
                groups.Add(new CustomDependencyGroup { Columns = refs });
        }
        return groups;
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
    /// Parses TablesToInclude from IConfiguration. Supports two forms:
    /// simple ("- dbo.Users") and structured ("- Table: dbo.Users / Columns: [...]").
    /// An empty list is not valid — TablesToInclude must contain at least one entry.
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

    /// <summary>
    /// Parses CustomValueLists from IConfiguration. Only the structured form is
    /// supported: each child must specify <c>Column</c> (schema.table.column)
    /// plus exactly one of <c>File</c> (path to a flat values file, one value
    /// per line) or <c>Values</c> (inline YAML list). The exactly-one-of rule
    /// is enforced by the validator so users get a friendly error message
    /// instead of silent drops.
    /// </summary>
    public static CustomValueList[] ParseCustomValueLists(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
            return [];

        var result = new List<CustomValueList>();
        foreach (var child in children)
        {
            var column = child["Column"];
            var file = child["File"];
            if (string.IsNullOrWhiteSpace(column))
                continue;

            var values = child.GetSection("Values").Get<string[]>();

            result.Add(new CustomValueList
            {
                Column = column,
                File = file ?? string.Empty,
                Values = values is { Length: > 0 } ? values.ToList() : null
            });
        }

        return result.ToArray();
    }
}
