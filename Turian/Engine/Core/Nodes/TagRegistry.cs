namespace Turian.Engine.Core;

/// <summary>Dense per-tag lists in node registration order, owned by one scene hierarchy.</summary>
sealed class TagRegistry
{
    readonly Dictionary<string, List<Node>> byTag = new(StringComparer.Ordinal);
    readonly Dictionary<Node, (long Order, HashSet<string> Tags)> registered = [];
    long nextOrder;

    internal void Register(Node node)
    {
        if (!registered.ContainsKey(node))
        {
            registered[node] = (nextOrder++, []);
        }

        Refresh(node);
    }

    internal void Unregister(Node node)
    {
        if (!registered.TryGetValue(node, out var entry))
        {
            return;
        }

        foreach (var tag in entry.Tags)
        {
            Remove(tag, node, entry.Order);
        }

        registered.Remove(node);
    }

    internal void Refresh(Node node)
    {
        if (!registered.TryGetValue(node, out var entry))
        {
            return;
        }

        var tags = node.Tags.Where(static tag => !string.IsNullOrWhiteSpace(tag)).ToHashSet(StringComparer.Ordinal);
        foreach (var tag in entry.Tags.Except(tags))
        {
            Remove(tag, node, entry.Order);
        }

        foreach (var tag in tags.Except(entry.Tags))
        {
            if (!byTag.TryGetValue(tag, out var nodes))
            {
                byTag[tag] = nodes = [];
            }

            nodes.Insert(IndexOf(nodes, entry.Order), node);
        }

        registered[node] = (entry.Order, tags);
    }

    internal Node? Find(string? tag) => tag is not null && byTag.TryGetValue(tag, out var nodes)
        ? nodes.FirstOrDefault(static node => node.IsActiveInHierarchy)
        : null;

    internal IReadOnlyList<Node> FindAll(string? tag) => tag is not null && byTag.TryGetValue(tag, out var nodes)
        ? nodes.Where(static node => node.IsActiveInHierarchy).ToArray()
        : [];

    void Remove(string tag, Node node, long order)
    {
        if (!byTag.TryGetValue(tag, out var nodes))
        {
            return;
        }

        var at = IndexOf(nodes, order);
        if (at < nodes.Count && ReferenceEquals(nodes[at], node))
        {
            nodes.RemoveAt(at);
        }

        if (nodes.Count == 0)
        {
            byTag.Remove(tag);
        }
    }

    // Every node starts Untagged, so one list can hold the whole scene; it stays sorted by registration order and is
    // bisected rather than scanned.
    int IndexOf(List<Node> nodes, long order)
    {
        int low = 0, high = nodes.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (registered[nodes[middle]].Order < order)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
