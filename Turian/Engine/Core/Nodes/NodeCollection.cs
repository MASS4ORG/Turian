using System.Collections;

namespace Turian.Engine.Core;

/// <summary>Ordered children with reference identity, amortized constant-time removal and versioned membership.</summary>
public sealed class NodeCollection : IList<Node>, IReadOnlyList<Node>
{
    readonly Node? owner;
    Node?[] slots = [];
    int start;
    int end;
    int live;
    int holes;
    int layout;
    int notificationDepth;
    int notifiedVersion;
    bool destructionNotifications;

    /// <summary>Creates an unowned builder for collection expressions and deserialization.</summary>
    public NodeCollection() { }

    internal NodeCollection(Node owner) => this.owner = owner;

    /// <summary>Gets the number of live children.</summary>
    public int Count => live;

    /// <summary>Gets whether this collection is read-only.</summary>
    public bool IsReadOnly => false;

    /// <summary>Gets the structural version; storage compaction does not change it.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after a structural operation, once per replacement or parent destruction batch.</summary>
    public event EventHandler? StructureChanged;

    /// <summary>Gets or replaces the child at a logical position among live children.</summary>
    public Node this[int index]
    {
        get => slots[LogicalSlot(index)]!;
        set
        {
            var previous = this[index];
            Validate(value);
            if (ReferenceEquals(previous, value)) { return; }
            using var change = DeferNotifications();
            using var prior = value.Parent?.Children.DeferNotifications();
            if (owner is not null) { value.Parent?.Children.RemoveCore(value, false); }
            var slot = owner is null ? LogicalSlot(index) : previous.SlotIndex;
            slots[slot] = value;
            if (owner is not null)
            {
                previous.SlotIndex = -1;
                previous.SetParentCore(null);
                value.SlotIndex = slot;
                value.SetParentCore(owner);
            }

            Changed();
        }
    }

    /// <summary>Appends a child, moving it from its previous parent or its existing position.</summary>
    public void Add(Node item)
    {
        Validate(item);
        using var change = DeferNotifications();
        using var prior = item.Parent?.Children.DeferNotifications();
        if (owner is not null)
        {
            if (Contains(item) && item.SlotIndex == end - 1) { return; }
            item.Parent?.Children.RemoveCore(item, false);
        }

        EnsureSpace();
        slots[end] = item;
        if (owner is not null) { item.SlotIndex = end; }
        end++;
        live++;
        if (owner is not null) { item.SetParentCore(owner); }
        Changed();
    }

    /// <summary>Inserts at a logical position counted after removing an existing listing of the child.</summary>
    public void Insert(int index, Node item)
    {
        Validate(item);
        var existing = owner is not null && Contains(item);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, live - (existing ? 1 : 0));
        using var change = DeferNotifications();
        using var prior = item.Parent?.Children.DeferNotifications();
        if (owner is not null) { item.Parent?.Children.RemoveCore(item, false); }
        CompactHoles();
        EnsureSpace();
        var slot = start + index;
        Array.Copy(slots, slot, slots, slot + 1, end - slot);
        slots[slot] = item;
        end++;
        live++;
        UpdateIndices(slot);
        if (owner is not null) { item.SetParentCore(owner); }
        Changed();
    }

    /// <summary>Removes the child by reference and parent identity.</summary>
    public bool Remove(Node item) => RemoveCore(item, true);

    /// <summary>Removes the child at a logical position.</summary>
    public void RemoveAt(int index) => RemoveSlot(LogicalSlot(index), true);

    /// <summary>Moves a child between logical positions while preserving its parent.</summary>
    public void Move(int oldIndex, int newIndex)
    {
        var oldSlot = LogicalSlot(oldIndex);
        var newSlot = LogicalSlot(newIndex);
        if (oldIndex == newIndex) { return; }
        var item = slots[oldSlot];
        if (oldSlot < newSlot)
        {
            Array.Copy(slots, oldSlot + 1, slots, oldSlot, newSlot - oldSlot);
        }
        else
        {
            Array.Copy(slots, newSlot, slots, newSlot + 1, oldSlot - newSlot);
        }

        slots[newSlot] = item;
        UpdateIndices(Math.Min(oldSlot, newSlot));
        Changed();
    }

    /// <summary>Removes all children and detaches their parent membership.</summary>
    public void Clear()
    {
        if (live == 0) { return; }
        for (var i = start; i < end; i++)
        {
            var item = slots[i];
            slots[i] = null;
            if (owner is not null && item is not null)
            {
                item.SlotIndex = -1;
                item.SetParentCore(null);
            }
        }

        start = end = live = holes = 0;
        Changed();
    }

    /// <summary>Copies an assigned sequence, retaining the first occurrence of each node reference.</summary>
    public void ReplaceWith(IEnumerable<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (ReferenceEquals(this, nodes)) { return; }
        var unique = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var replacement = new List<Node>();
        foreach (var item in nodes)
        {
            Validate(item);
            if (unique.Add(item)) { replacement.Add(item); }
        }

        using var change = DeferNotifications();
        if (owner is null) { Clear(); }
        foreach (var item in replacement) { Add(item); }
        if (owner is not null) { RemoveExcluded(unique); }
    }

    void RemoveExcluded(HashSet<Node> retained)
    {
        if (retained.Count == 0)
        {
            Clear();
            return;
        }

        foreach (var item in this.ToArray())
        {
            if (!retained.Contains(item)) { Remove(item); }
        }
    }

    /// <summary>Checks membership using reference identity.</summary>
    public bool Contains(Node item) => FindSlot(item) >= 0;

    /// <summary>Finds the logical position by reference identity, compacting interior holes if necessary.</summary>
    public int IndexOf(Node item)
    {
        CompactHoles();
        var slot = FindSlot(item);
        return slot < 0 ? -1 : slot - start;
    }

    /// <summary>Copies the live children in sibling order.</summary>
    public void CopyTo(Node[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (arrayIndex > array.Length || live > array.Length - arrayIndex)
        {
            throw new ArgumentException("The destination has insufficient space.", nameof(array));
        }

        foreach (var item in this) { array[arrayIndex++] = item; }
    }

    /// <summary>Enumerates live children without allocating.</summary>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<Node> IEnumerable<Node>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    bool RemoveCore(Node item, bool detach)
    {
        var slot = FindSlot(item);
        if (slot < 0) { return false; }
        RemoveSlot(slot, detach);
        return true;
    }

    void RemoveSlot(int slot, bool detach)
    {
        var item = slots[slot]!;
        slots[slot] = null;
        live--;
        if (slot == start || slot == end - 1)
        {
            Trim(slot);
        }
        else
        {
            holes++;
        }

        if (owner is not null)
        {
            item.SlotIndex = -1;
            if (detach) { item.SetParentCore(null); }
        }

        if (holes > Math.Max(16, live / 8)) { Compact(); }
        Changed();
    }

    void Trim(int removedSlot)
    {
        while (start < end && slots[start] is null)
        {
            if (start != removedSlot) { holes--; }
            start++;
        }

        while (end > start && slots[end - 1] is null)
        {
            if (end - 1 != removedSlot) { holes--; }
            end--;
        }
    }

    int FindSlot(Node? item)
    {
        if (item is null) { return -1; }
        if (owner is not null)
        {
            return FindOwnedSlot(item);
        }

        for (var i = start; i < end; i++)
        {
            if (ReferenceEquals(slots[i], item)) { return i; }
        }

        return -1;
    }

    int FindOwnedSlot(Node item)
    {
        var index = item.SlotIndex;
        return ReferenceEquals(item.Parent, owner) && index >= start && index < end
            && ReferenceEquals(slots[index], item) ? index : -1;
    }

    int LogicalSlot(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, live);
        CompactHoles();
        return start + index;
    }

    void Validate(Node item)
    {
        ArgumentNullException.ThrowIfNull(item);
        for (var ancestor = owner; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, item))
            {
                throw new ArgumentException("A node cannot be its own ancestor.", nameof(item));
            }
        }
    }

    void Changed()
    {
        Version = unchecked(Version + 1);
        if (owner is not null) { Node.NotifyHierarchyChanged(); }
        PublishChange();
    }

    ChangeScope DeferNotifications() => new(this);

    internal bool BeginDestructionNotifications()
    {
        if (destructionNotifications) { return false; }
        destructionNotifications = true;
        notificationDepth++;
        return true;
    }

    internal void EndDestructionNotifications()
    {
        destructionNotifications = false;
        EndNotifications();
    }

    void EndNotifications()
    {
        notificationDepth--;
        PublishChange();
    }

    void PublishChange()
    {
        if (notificationDepth != 0 || notifiedVersion == Version) { return; }
        notifiedVersion = Version;
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    readonly struct ChangeScope : IDisposable
    {
        readonly NodeCollection collection;

        internal ChangeScope(NodeCollection collection)
        {
            this.collection = collection;
            collection.notificationDepth++;
        }

        /// <summary>Publishes the structural notification when the outermost operation completes.</summary>
        public void Dispose() => collection.EndNotifications();
    }

    void EnsureSpace()
    {
        if (end < slots.Length) { return; }
        Compact();
        if (end == slots.Length) { Array.Resize(ref slots, Math.Max(4, slots.Length * 2)); }
    }

    void CompactHoles()
    {
        if (holes > 0) { Compact(); }
    }

    void Compact()
    {
        if (start == 0 && holes == 0) { return; }
        var write = 0;
        for (var read = start; read < end; read++)
        {
            if (slots[read] is not { } item) { continue; }
            slots[write] = item;
            if (owner is not null) { item.SlotIndex = write; }
            write++;
        }

        Array.Clear(slots, write, end - write);
        start = holes = 0;
        end = write;
        layout++;
    }

    void UpdateIndices(int from)
    {
        if (owner is null) { return; }
        for (var i = from; i < end; i++) { slots[i]!.SlotIndex = i; }
    }

    /// <summary>Skips vacant slots, remaps across compaction and rejects structural mutation.</summary>
    public struct Enumerator : IEnumerator<Node>
    {
        readonly NodeCollection collection;
        readonly int version;
        int layout;
        int cursor;
        int yielded;

        internal Enumerator(NodeCollection collection)
        {
            this.collection = collection;
            version = collection.Version;
            layout = collection.layout;
            cursor = collection.start;
        }

        /// <summary>Gets the current child.</summary>
        public Node Current { readonly get; private set; } = null!;
        readonly object IEnumerator.Current => Current;

        /// <summary>Advances to the next live child.</summary>
        public bool MoveNext()
        {
            if (version != collection.Version)
            {
                throw new InvalidOperationException("The children changed during enumeration.");
            }

            if (layout != collection.layout)
            {
                cursor = collection.start + yielded;
                layout = collection.layout;
            }

            while (cursor < collection.end)
            {
                if (collection.slots[cursor++] is not { } item) { continue; }
                Current = item;
                yielded++;
                return true;
            }

            Current = null!;
            return false;
        }

        /// <summary>Releases the enumerator's resources.</summary>
        public readonly void Dispose() { }

        readonly void IEnumerator.Reset() => throw new NotSupportedException();
    }
}
