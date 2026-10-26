namespace Turian.Engine.Core;

/// <summary>
/// Maintains pluggable node categories from structural changes, normalizing each listing into hierarchy order only
/// when read after a change. Consumers filter activity and other mutable values when reading.
/// </summary>
sealed class SceneIndex(Node root)
{
    /// <summary>A structural membership rule shared by every scene that a consumer queries.</summary>
    internal sealed class Category(Func<Node, bool> matches)
    {
        internal bool Matches(Node node) => !node.IsDestroyed && matches(node);
    }

    /// <summary>The category containing nodes with components assignable to a type or interface.</summary>
    internal static Category For<T>() where T : class => ComponentCategory<T>.Instance;

    readonly Dictionary<Category, Listing> listings = [];
    int version;

    /// <summary>The category's nodes in hierarchy order, including inactive ones; not to be modified.</summary>
    internal List<Node> Nodes(Category category)
    {
        if (!listings.TryGetValue(category, out var listing))
        {
            listing = new Listing(category);
            listings.Add(category, listing);
            listing.Seed(root);
        }

        if (listing.Version != version)
        {
            listing.Normalize(this);
            listing.Version = version;
        }

        return listing.Nodes;
    }

    internal void Register(Node node) => Refresh(node);

    internal void Unregister(Node node)
    {
        foreach (var listing in listings.Values)
        {
            if (listing.SetMembership(node, false)) { version++; }
        }
    }

    /// <summary>Reclassifies a node after its components changed or started.</summary>
    internal void Refresh(Node node)
    {
        foreach (var listing in listings.Values)
        {
            if (listing.SetMembership(node, listing.Category.Matches(node))) { version++; }
        }
    }

    /// <summary>Sibling order or membership changed somewhere in the hierarchy.</summary>
    internal void OrderChanged() => version++;

    static class ComponentCategory<T> where T : class
    {
        internal static readonly Category Instance = new(static node =>
        {
            foreach (var component in node.Components)
            {
                if (component is T) { return true; }
            }

            return false;
        });
    }

    // Preorder position: an ancestor precedes its descendants, and siblings follow their storage slots, which keep
    // sibling order.
    static int Compare(Node a, Node b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        int depthA = Depth(a), depthB = Depth(b);
        var (left, right) = (a, b);
        for (; depthA > depthB; depthA--)
        {
            left = left.Parent!;
        }

        for (; depthB > depthA; depthB--)
        {
            right = right.Parent!;
        }

        if (ReferenceEquals(left, right))
        {
            return ReferenceEquals(left, a) ? -1 : 1;
        }

        while (!ReferenceEquals(left.Parent, right.Parent))
        {
            (left, right) = (left.Parent!, right.Parent!);
        }

        return left.SlotIndex.CompareTo(right.SlotIndex);
    }

    static int Depth(Node node)
    {
        var depth = 0;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            depth++;
        }

        return depth;
    }

    sealed class HierarchyComparer : IComparer<Node>
    {
        internal static readonly HierarchyComparer Instance = new();

        public int Compare(Node? x, Node? y) => SceneIndex.Compare(x!, y!);
    }

    // Membership and physical entries are separate so leaving and rejoining between reads never appends twice.
    sealed class Listing(Category category)
    {
        readonly Dictionary<Node, bool> membership = new(ReferenceEqualityComparer.Instance);
        internal Category Category { get; } = category;
        internal List<Node> Nodes { get; } = [];
        internal int Version { get; set; } = -1;

        internal void Seed(Node node)
        {
            SetMembership(node, Category.Matches(node));
            foreach (var child in node.Children) { Seed(child); }
        }

        internal bool SetMembership(Node node, bool included)
        {
            if (membership.TryGetValue(node, out var previous))
            {
                if (previous == included) { return false; }
                membership[node] = included;
                return true;
            }

            if (included)
            {
                membership.Add(node, true);
                Nodes.Add(node);
            }

            return included;
        }

        internal void Normalize(SceneIndex index)
        {
            var write = 0;
            for (var read = 0; read < Nodes.Count; read++)
            {
                var node = Nodes[read];
                if (membership[node] && ReferenceEquals(node.BoundIndex, index))
                {
                    Nodes[write++] = node;
                }
                else
                {
                    membership.Remove(node);
                }
            }

            Nodes.RemoveRange(write, Nodes.Count - write);
            Nodes.Sort(HierarchyComparer.Instance);
        }
    }
}
