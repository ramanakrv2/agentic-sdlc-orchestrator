namespace Orchestrator.Core.Engine;

/// <summary>
/// Explicit, mutable dependency graph. Mutable because planners expand it at runtime and re-planning prunes it.
/// Validation guarantees it stays acyclic and fully resolved after every change.
/// </summary>
public sealed class WorkflowGraph
{
    private readonly Dictionary<string, NodeDefinition> _nodes = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public IEnumerable<NodeDefinition> Nodes => _order.Select(id => _nodes[id]);

    public NodeDefinition this[string id] => _nodes[id];

    public bool Contains(string id) => _nodes.ContainsKey(id);

    public WorkflowGraph Add(NodeDefinition node)
    {
        if (!_nodes.TryAdd(node.Id, node))
            throw new InvalidOperationException($"Duplicate node id '{node.Id}'.");
        _order.Add(node.Id);
        return this;
    }

    public void AddRange(IEnumerable<NodeDefinition> nodes)
    {
        var added = new List<string>();
        try
        {
            foreach (var n in nodes) { Add(n); added.Add(n.Id); }
            Validate();
        }
        catch
        {
            foreach (var id in added) Remove(id);
            throw;
        }
    }

    public void Remove(string id)
    {
        if (_nodes.Remove(id)) _order.Remove(id);
    }

    /// <summary>Resolved direct dependencies, expanding "@group" references.</summary>
    public IEnumerable<string> DependenciesOf(string id)
    {
        foreach (var dep in _nodes[id].DependsOn)
        {
            if (dep.StartsWith('@'))
            {
                var group = dep[1..];
                foreach (var n in _nodes.Values.Where(n => n.Group == group && n.Id != id)) yield return n.Id;
            }
            else
            {
                yield return dep;
            }
        }
    }

    public IEnumerable<string> DependentsOf(string id) =>
        _order.Where(other => DependenciesOf(other).Contains(id, StringComparer.Ordinal));

    /// <summary>All transitive downstream nodes (used to invalidate on re-plan).</summary>
    public IReadOnlySet<string> DescendantsOf(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(DependentsOf(id));
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur)) continue;
            foreach (var d in DependentsOf(cur)) stack.Push(d);
        }
        return seen;
    }

    /// <summary>Throws if a dependency is unknown or the graph has a cycle. Returns a topological order.</summary>
    public IReadOnlyList<string> Validate()
    {
        foreach (var node in _nodes.Values)
        foreach (var dep in node.DependsOn.Where(d => !d.StartsWith('@')))
            if (!_nodes.ContainsKey(dep))
                throw new InvalidOperationException($"Node '{node.Id}' depends on unknown node '{dep}'.");

        var inDegree = _order.ToDictionary(id => id, id => DependenciesOf(id).Distinct().Count(), StringComparer.Ordinal);
        var queue = new Queue<string>(_order.Where(id => inDegree[id] == 0));
        var result = new List<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            result.Add(id);
            foreach (var dependent in DependentsOf(id))
                if (--inDegree[dependent] == 0) queue.Enqueue(dependent);
        }

        if (result.Count != _order.Count)
        {
            var cyclic = _order.Except(result);
            throw new InvalidOperationException($"Workflow graph has a cycle involving: {string.Join(", ", cyclic)}");
        }
        return result;
    }
}
