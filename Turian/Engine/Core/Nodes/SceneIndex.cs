namespace Turian.Engine.Core;

/// <summary>
/// The nodes of one scene hierarchy that the game loop and the renderer look up every frame, kept current from
/// structural changes rather than searched for: nodes with frame callbacks or unstarted components, nodes with
/// cameras and nodes with statistics displays. A list is compacted and sorted into hierarchy order when read after a
/// change, so steady frames do no work.
/// </summary>
sealed class SceneIndex
{
    /// <summary>What a node is listed for.</summary>
    [Flags]
    internal enum Kinds : byte
    {
        None = 0,
        Callbacks = 1,
        Unstarted = 2,
        Cameras = 4,
        Statistics = 8,
    }

    const Component.FrameCallbacks AnyCallback =
        Component.FrameCallbacks.Update | Component.FrameCallbacks.LateUpdate | Component.FrameCallbacks.FixedUpdate;

    readonly Listing callbacks = new(Kinds.Callbacks);
    readonly Listing cameras = new(Kinds.Cameras);
    readonly Listing statistics = new(Kinds.Statistics);
    int version;

    /// <summary>The listed nodes of one kind in hierarchy order, including inactive ones; not to be modified.</summary>
    internal List<Node> Nodes(Kinds kind)
    {
        var listing = kind switch
        {
            Kinds.Callbacks => callbacks,
            Kinds.Cameras => cameras,
            Kinds.Statistics => statistics,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        if (listing.Version != version)
        {
            listing.Normalize(this);
            listing.Version = version;
        }

        return listing.Nodes;
    }

    internal void Register(Node node)
    {
        node.IndexedKinds = Kinds.None;
        Refresh(node);
    }

    internal void Unregister(Node node)
    {
        if (node.IndexedKinds != Kinds.None)
        {
            node.IndexedKinds = Kinds.None;
            version++;
        }
    }

    /// <summary>Reclassifies a node after its components changed or started.</summary>
    internal void Refresh(Node node)
    {
        var kinds = node.IsDestroyed ? Kinds.None : Classify(node);
        if (kinds == node.IndexedKinds)
        {
            return;
        }

        if (!ReferenceEquals(node.ListedIndex, this))
        {
            node.ListedIndex = this;
            node.ListedKinds = Kinds.None;
        }

        node.IndexedKinds = kinds;
        callbacks.Append(node, kinds);
        cameras.Append(node, kinds);
        statistics.Append(node, kinds);
        version++;
    }

    /// <summary>Sibling order or membership changed somewhere in the hierarchy.</summary>
    internal void OrderChanged() => version++;

    static Kinds Classify(Node node)
    {
        var kinds = Node.OverridesFrameCallbacks(node.GetType()) ? Kinds.Callbacks : Kinds.None;
        var components = node.Components;
        for (var i = 0; i < components.Count; i++)
        {
            var component = components[i];
            if (!component.IsStarted)
            {
                kinds |= Kinds.Callbacks | Kinds.Unstarted;
            }
            else if (component.Overrides(AnyCallback))
            {
                kinds |= Kinds.Callbacks;
            }

            if (component is CameraComponent)
            {
                kinds |= Kinds.Cameras;
            }

            if (component is IRenderStatisticsTarget)
            {
                kinds |= Kinds.Statistics;
            }
        }

        return kinds;
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

    // Nodes are appended when they gain a kind and dropped lazily, so a node that leaves and rejoins between reads is
    // listed once.
    sealed class Listing(Kinds kind)
    {
        internal List<Node> Nodes { get; } = [];
        internal int Version { get; set; } = -1;

        internal void Append(Node node, Kinds kinds)
        {
            if ((kinds & kind) != 0 && (node.ListedKinds & kind) == 0)
            {
                node.ListedKinds |= kind;
                Nodes.Add(node);
            }
        }

        internal void Normalize(SceneIndex index)
        {
            var write = 0;
            for (var read = 0; read < Nodes.Count; read++)
            {
                if (Keeps(Nodes[read], index))
                {
                    Nodes[write++] = Nodes[read];
                }
            }

            Nodes.RemoveRange(write, Nodes.Count - write);
            Nodes.Sort(HierarchyComparer.Instance);

            // A node that rejoined through another hierarchy between reads was appended twice.
            write = 0;
            for (var read = 0; read < Nodes.Count; read++)
            {
                if (write == 0 || !ReferenceEquals(Nodes[write - 1], Nodes[read]))
                {
                    Nodes[write++] = Nodes[read];
                }
            }

            Nodes.RemoveRange(write, Nodes.Count - write);
        }

        bool Keeps(Node node, SceneIndex index)
        {
            if (!ReferenceEquals(node.ListedIndex, index))
            {
                return false;
            }

            if (ReferenceEquals(node.BoundIndex, index) && (node.IndexedKinds & kind) != 0)
            {
                return true;
            }

            node.ListedKinds &= ~kind;
            return false;
        }
    }
}
