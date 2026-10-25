namespace Turian.Engine.Core;

/// <summary>
/// Dense per-tag lists in node registration order, owned by one scene hierarchy. Untagged, which every node starts
/// with, is implicit: it is answered from the registrations rather than kept as a list of the whole scene.
/// </summary>
sealed class TagRegistry
{
    const string Untagged = "Untagged";

    // Shared read-only tag sets for the common cases, so registering an ordinary node allocates no set.
    static readonly HashSet<string> NoTags = new(StringComparer.Ordinal);
    static readonly HashSet<string> UntaggedOnly = new(StringComparer.Ordinal) { Untagged };

    readonly Dictionary<string, List<Node>> byTag = new(StringComparer.Ordinal);
    readonly Dictionary<Node, (long Order, HashSet<string> Tags)> registered = [];
    long nextOrder;

    internal void Register(Node node)
    {
        if (!registered.ContainsKey(node))
        {
            registered[node] = (nextOrder++, NoTags);
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

        var tags = TagsOf(node);
        foreach (var tag in entry.Tags)
        {
            if (!tags.Contains(tag))
            {
                Remove(tag, node, entry.Order);
            }
        }

        foreach (var tag in tags)
        {
            if (!entry.Tags.Contains(tag))
            {
                Insert(tag, node, entry.Order);
            }
        }

        registered[node] = (entry.Order, tags);
    }

    internal Node? Find(string? tag)
    {
        if (tag == Untagged)
        {
            Node? first = null;
            var firstOrder = long.MaxValue;
            foreach (var (node, order) in FindUntagged())
            {
                if (order < firstOrder)
                {
                    (first, firstOrder) = (node, order);
                }
            }

            return first;
        }

        return tag is not null && byTag.TryGetValue(tag, out var nodes)
            ? nodes.FirstOrDefault(static node => node.IsActiveInHierarchy)
            : null;
    }

    internal IReadOnlyList<Node> FindAll(string? tag)
    {
        if (tag == Untagged)
        {
            return [.. FindUntagged().OrderBy(static match => match.Order).Select(static match => match.Node)];
        }

        return tag is not null && byTag.TryGetValue(tag, out var nodes)
            ? nodes.Where(static node => node.IsActiveInHierarchy).ToArray()
            : [];
    }

    // A scan of every registration: Untagged queries are rare, while every spawn and despawn would pay for a list.
    IEnumerable<(Node Node, long Order)> FindUntagged() => registered
        .Where(static pair => pair.Value.Tags.Contains(Untagged) && pair.Key.IsActiveInHierarchy)
        .Select(static pair => (pair.Key, pair.Value.Order));

    static HashSet<string> TagsOf(Node node)
    {
        var source = node.Tags;
        if (source.Count == 0)
        {
            return NoTags;
        }

        if (source.Count == 1 && source[0] == Untagged)
        {
            return UntaggedOnly;
        }

        return source.Where(static tag => !string.IsNullOrWhiteSpace(tag)).ToHashSet(StringComparer.Ordinal);
    }

    void Insert(string tag, Node node, long order)
    {
        if (tag == Untagged)
        {
            return;
        }

        if (!byTag.TryGetValue(tag, out var nodes))
        {
            byTag[tag] = nodes = [];
        }

        nodes.Insert(IndexOf(nodes, order), node);
    }

    void Remove(string tag, Node node, long order)
    {
        if (tag == Untagged || !byTag.TryGetValue(tag, out var nodes))
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

    // Tag lists stay sorted by registration order, so they are bisected rather than scanned.
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
