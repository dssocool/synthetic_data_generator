using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class DependencyGraph
{
    private readonly Dictionary<string, HashSet<string>> _adjacency = new();
    private readonly Dictionary<string, int> _inDegree = new();
    private readonly Dictionary<string, TableInfo> _tableMap = new();
    private readonly HashSet<string> _selfReferencingTables = new();

    public IReadOnlySet<string> SelfReferencingTables => _selfReferencingTables;

    public void Build(
        List<TableInfo> tables,
        Dictionary<string, HashSet<string>>? columnsInScope = null)
    {
        foreach (var table in tables)
        {
            var name = table.FullName;
            _tableMap[name] = table;
            _adjacency.TryAdd(name, []);
            _inDegree.TryAdd(name, 0);
        }

        foreach (var table in tables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (fk.IsSelfReferencing)
                {
                    _selfReferencingTables.Add(table.FullName);
                    continue;
                }

                if (columnsInScope is not null
                    && columnsInScope.TryGetValue(table.FullName, out var scopedCols)
                    && !scopedCols.Contains(fk.ParentColumn))
                    continue;

                var from = fk.FullReferencedTableName;
                var to = fk.FullParentTableName;

                if (!_adjacency.ContainsKey(from) || !_adjacency.ContainsKey(to))
                    continue;

                if (_adjacency[from].Add(to))
                    _inDegree[to]++;
            }
        }
    }

    /// <summary>
    /// Adds directed edges for custom (business-logic) column dependencies.
    /// The first column reference in each group is the source; subsequent tables depend on it.
    /// Only adds edges for tables that are already in the graph (in scope).
    /// </summary>
    public void AddCustomDependencies(List<CustomDependencyGroup>? groups)
    {
        if (groups is null or { Count: 0 })
            return;

        foreach (var group in groups)
        {
            if (group.Columns.Count < 2)
                continue;

            var sourceTable = group.Columns[0].Table;
            if (!_adjacency.ContainsKey(sourceTable))
                continue;

            for (var i = 1; i < group.Columns.Count; i++)
            {
                var depTable = group.Columns[i].Table;
                if (!_adjacency.ContainsKey(depTable))
                    continue;

                if (string.Equals(sourceTable, depTable, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_adjacency[sourceTable].Add(depTable))
                    _inDegree[depTable]++;
            }
        }
    }

    /// <summary>
    /// Returns tables in topological order using Kahn's algorithm.
    /// Tables with no FK dependencies come first.
    /// </summary>
    public List<TableInfo> GetTopologicalOrder()
    {
        var inDegree = new Dictionary<string, int>(_inDegree);
        var queue = new Queue<string>();

        foreach (var (node, degree) in inDegree)
        {
            if (degree == 0)
                queue.Enqueue(node);
        }

        var sorted = new List<TableInfo>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(_tableMap[current]);

            if (!_adjacency.TryGetValue(current, out var neighbors))
                continue;

            foreach (var neighbor in neighbors)
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        if (sorted.Count != _tableMap.Count)
        {
            var cycleNodes = _tableMap.Keys
                .Except(sorted.Select(t => t.FullName))
                .ToList();

            throw new InvalidOperationException(
                $"Circular FK dependency detected among tables: {string.Join(", ", cycleNodes)}. " +
                "Cannot determine a valid insertion order.");
        }

        return sorted;
    }
}
