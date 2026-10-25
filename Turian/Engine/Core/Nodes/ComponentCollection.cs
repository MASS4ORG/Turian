using System.Collections;

namespace Turian.Engine.Core;

/// <summary>
/// A node's ordered components with reference identity and versioned membership. Editing the collection changes
/// membership only; <see cref="Node.AddComponent(Component)"/>, <see cref="Node.RemoveComponent(Component)"/> and
/// <see cref="Component.Destroy"/> also run the component lifecycle.
/// </summary>
public sealed class ComponentCollection : IList<Component>, IReadOnlyList<Component>
{
    readonly Node? owner;
    readonly List<Component> items = [];
    int notificationDepth;
    int notifiedVersion;

    /// <summary>Creates an unowned builder for collection expressions and deserialization.</summary>
    public ComponentCollection() { }

    internal ComponentCollection(Node owner) => this.owner = owner;

    /// <summary>Gets the number of components.</summary>
    public int Count => items.Count;

    /// <summary>Gets whether this collection is read-only.</summary>
    public bool IsReadOnly => false;

    /// <summary>Gets the structural version.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after a structural operation, once per replacement.</summary>
    public event EventHandler? StructureChanged;

    /// <summary>Gets or replaces the component at a position.</summary>
    public Component this[int index]
    {
        get => items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var previous = items[index];
            if (ReferenceEquals(previous, value)) { return; }
            using var change = DeferNotifications();
            Unlist(value);
            items[ReferenceIndex(previous)] = value;
            Listed(previous, false);
            Listed(value, true);
            Changed();
        }
    }

    /// <summary>Appends a component, moving it from another node's listing or from its existing position.</summary>
    public void Add(Component item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (owner is not null && items.Count > 0 && ReferenceEquals(items[^1], item)) { return; }
        using var change = DeferNotifications();
        Unlist(item);
        items.Add(item);
        Listed(item, true);
        Changed();
    }

    /// <summary>Inserts at a position counted after removing an existing listing of the component.</summary>
    public void Insert(int index, Component item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var existing = owner is not null && Contains(item);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, items.Count - (existing ? 1 : 0));
        using var change = DeferNotifications();
        Unlist(item);
        items.Insert(index, item);
        Listed(item, true);
        Changed();
    }

    /// <summary>Removes the component by reference.</summary>
    public bool Remove(Component item)
    {
        var index = IndexOf(item);
        if (index < 0) { return false; }
        RemoveAt(index);
        return true;
    }

    /// <summary>Removes the component at a position.</summary>
    public void RemoveAt(int index)
    {
        var item = items[index];
        items.RemoveAt(index);
        Listed(item, false);
        Changed();
    }

    /// <summary>Moves a component between positions.</summary>
    public void Move(int oldIndex, int newIndex)
    {
        var item = items[oldIndex];
        ArgumentOutOfRangeException.ThrowIfNegative(newIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(newIndex, items.Count);
        if (oldIndex == newIndex) { return; }
        items.RemoveAt(oldIndex);
        items.Insert(newIndex, item);
        Changed();
    }

    /// <summary>Removes every component from the listing.</summary>
    public void Clear()
    {
        if (items.Count == 0) { return; }
        foreach (var item in items) { Listed(item, false); }
        items.Clear();
        Changed();
    }

    /// <summary>Copies an assigned sequence, retaining the first occurrence of each component reference.</summary>
    public void ReplaceWith(IEnumerable<Component> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (ReferenceEquals(this, components)) { return; }
        var unique = new HashSet<Component>(ReferenceEqualityComparer.Instance);
        var replacement = new List<Component>();
        foreach (var item in components)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (unique.Add(item)) { replacement.Add(item); }
        }

        using var change = DeferNotifications();
        Clear();
        foreach (var item in replacement) { Add(item); }
    }

    /// <summary>Checks membership using reference identity.</summary>
    public bool Contains(Component item) => IndexOf(item) >= 0;

    /// <summary>Finds the position by reference identity.</summary>
    public int IndexOf(Component item)
    {
        if (item is null || (owner is not null && !ReferenceEquals(item.ListedIn, this))) { return -1; }
        return ReferenceIndex(item);
    }

    // IdObject equality compares ids, so membership compares references explicitly.
    int ReferenceIndex(Component item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item)) { return i; }
        }

        return -1;
    }

    /// <summary>Copies the components in order.</summary>
    public void CopyTo(Component[] array, int arrayIndex) => items.CopyTo(array, arrayIndex);

    /// <summary>Enumerates the components without allocating.</summary>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<Component> IEnumerable<Component>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // An owned listing is exclusive, so listing a component here removes it from any node's components, this one
    // included. An unowned builder is a plain list.
    void Unlist(Component item)
    {
        if (owner is not null) { item.ListedIn?.RemoveListing(item); }
    }

    void RemoveListing(Component item)
    {
        var index = ReferenceIndex(item);
        if (index < 0) { return; }
        using var change = DeferNotifications();
        items.RemoveAt(index);
        item.ListedIn = null;
        Changed();
    }

    void Listed(Component item, bool listed)
    {
        if (owner is null) { return; }
        item.ListedIn = listed ? this : null;
    }

    void Changed()
    {
        Version = unchecked(Version + 1);
        owner?.ComponentsChanged();
        PublishChange();
    }

    ChangeScope DeferNotifications() => new(this);

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
        readonly ComponentCollection collection;

        internal ChangeScope(ComponentCollection collection)
        {
            this.collection = collection;
            collection.notificationDepth++;
        }

        /// <summary>Publishes the structural notification when the outermost operation completes.</summary>
        public void Dispose() => collection.EndNotifications();
    }

    /// <summary>Enumerates in order and rejects structural mutation.</summary>
    public struct Enumerator : IEnumerator<Component>
    {
        readonly ComponentCollection collection;
        readonly int version;
        int index;

        internal Enumerator(ComponentCollection collection)
        {
            this.collection = collection;
            version = collection.Version;
            index = -1;
        }

        /// <summary>Gets the current component.</summary>
        public readonly Component Current => collection.items[index];
        readonly object IEnumerator.Current => Current;

        /// <summary>Advances to the next component.</summary>
        public bool MoveNext()
        {
            if (version != collection.Version)
            {
                throw new InvalidOperationException("The components changed during enumeration.");
            }

            return ++index < collection.items.Count;
        }

        /// <summary>Releases the enumerator's resources.</summary>
        public readonly void Dispose() { }

        readonly void IEnumerator.Reset() => throw new NotSupportedException();
    }
}
