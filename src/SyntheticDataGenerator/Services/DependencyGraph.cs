using SyntheticDataGenerator.Models;

namespace SyntheticDataGenerator.Services;

public class DependencyGraph
{
    private readonly Dictionary<string, HashSet<string>> _adjacency = new();
    private readonly Dictionary<string, int> _inDegree = new();
    private readonly Dictionary<string, TableInfo> _tableMap = new();
    private readonly HashSet<string> _selfReferencingTables = new();

    public IReadOnlySet<string> SelfReferencingTables => _selfReferencingTables;

    public void Build(List<TableInfo> tables)
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
