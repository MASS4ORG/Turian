namespace Turian.Engine.Core;

/// <summary>Dense per-tag lists in node registration order, owned by one scene hierarchy.</summary>
sealed class TagRegistry
{
    readonly Dictionary<string, List<Node>> byTag = new(StringComparer.Ordinal);
    readonly Dictionary<Node, (long Order, HashSet<string> Tags)> registered = [];
    long nextOrder;

    internal void Register(Node node)
    {
        if (!registered.ContainsKey(node)) registered[node] = (nextOrder++, []);
        Refresh(node);
    }

    internal void Unregister(Node node)
    {
        if (!registered.Remove(node, out var entry)) return;
        foreach (var tag in entry.Tags) Remove(tag, node);
    }

    internal void Refresh(Node node)
    {
        if (!registered.TryGetValue(node, out var entry)) return;
        var tags = node.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToHashSet(StringComparer.Ordinal);
        foreach (var tag in entry.Tags.Except(tags)) Remove(tag, node);
        foreach (var tag in tags.Except(entry.Tags))
        {
            if (!byTag.TryGetValue(tag, out var nodes)) byTag[tag] = nodes = [];
            var at = nodes.FindIndex(other => registered[other].Order > entry.Order);
            nodes.Insert(at < 0 ? nodes.Count : at, node);
        }

        registered[node] = (entry.Order, tags);
    }

    internal Node? Find(string? tag) => tag is not null && byTag.TryGetValue(tag, out var nodes)
        ? nodes.FirstOrDefault(node => node.IsActiveInHierarchy)
        : null;

    internal IReadOnlyList<Node> FindAll(string? tag) => tag is not null && byTag.TryGetValue(tag, out var nodes)
        ? nodes.Where(node => node.IsActiveInHierarchy).ToArray()
        : [];

    void Remove(string tag, Node node)
    {
        if (!byTag.TryGetValue(tag, out var nodes)) return;
        nodes.Remove(node);
        if (nodes.Count == 0) byTag.Remove(tag);
    }
}
