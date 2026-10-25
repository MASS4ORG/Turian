using System.Collections;
using System.Collections.Specialized;

namespace Turian.Engine.Core;

public partial class Node
{
    TagRegistry? tagRegistry;
    LayerFilter? layerFilter;
    // How many times each child is listed, by reference, so add, remove and reparent need no scan of Children.
    Dictionary<Node, int> childEntries = new(ReferenceEqualityComparer.Instance);

    /// <summary>Creates a node with observable tags and hierarchy membership.</summary>
    public Node()
    {
        Tags.CollectionChanged += TagsChanged;
        Children.CollectionChanged += ChildrenChanged;
    }

    /// <summary>The node's tags, serialized as strings; absent serialized tags default to Untagged.</summary>
    public ObservableCollection<string> Tags
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field.CollectionChanged -= TagsChanged;
            field = value;
            field.CollectionChanged += TagsChanged;
            tagRegistry?.Refresh(this);
        }
    } = ["Untagged"];

    /// <summary>The serialized physics layer index, independent of the rendering layer.</summary>
    public int PhysicsLayer
    {
        get;
        set => field = layerFilter?.ResolvePhysics(value).Index ?? value;
    }

    /// <summary>The serialized rendering layer index, independent of the physics layer.</summary>
    public int RenderLayer
    {
        get;
        set => field = layerFilter?.ResolveRender(value).Index ?? value;
    }

    /// <summary>The physics slot's stable identity resolved through the bound project settings.</summary>
    [JsonIgnore, Hide]
    public Guid PhysicsLayerId => layerFilter?.ResolvePhysics(PhysicsLayer).Id ?? Guid.Empty;

    /// <summary>The rendering slot's stable identity resolved through the bound project settings.</summary>
    [JsonIgnore, Hide]
    public Guid RenderLayerId => layerFilter?.ResolveRender(RenderLayer).Id ?? Guid.Empty;

    /// <summary>Whether this node and its ancestors are active and the node has not been destroyed.</summary>
    [JsonIgnore, Hide]
    public bool IsActiveInHierarchy { get; private set; } = true;

    /// <summary>Finds the first active node with an exact tag in this node's scene registry.</summary>
    public Node? FindWithTag(string? tag) => EnsureTagRegistry().Find(tag);

    /// <summary>Finds active nodes with an exact tag in registration order, without a hierarchy walk.</summary>
    public IReadOnlyList<Node> FindAllWithTag(string? tag) => EnsureTagRegistry().FindAll(tag);

    internal void BindTagRegistry(TagRegistry? registry)
    {
        if (ReferenceEquals(tagRegistry, registry))
        {
            return;
        }

        tagRegistry?.Unregister(this);
        tagRegistry = registry;
        if (!IsDestroyed)
        {
            registry?.Register(this);
        }

        foreach (var child in Children)
        {
            child.BindTagRegistry(registry);
        }
    }

    internal void RefreshActiveHierarchy()
    {
        IsActiveInHierarchy = IsActive && !IsDestroyed && (Parent?.IsActiveInHierarchy ?? true);
        foreach (var child in Children)
        {
            child.RefreshActiveHierarchy();
        }
    }

    void BindLayers()
    {
        layerFilter = Services?.GetService<LayerFilter>() ?? Parent?.layerFilter
                      ?? new LayerFilter(Services?.GetService<IAppSettings>());
        PhysicsLayer = PhysicsLayer;
        RenderLayer = RenderLayer;
    }

    TagRegistry EnsureTagRegistry()
    {
        if (tagRegistry is not null)
        {
            return tagRegistry;
        }

        if (Parent is not null)
        {
            return Parent.EnsureTagRegistry();
        }

        BindTagRegistry(new TagRegistry());
        return tagRegistry!;
    }

    void TagsChanged(object? sender, NotifyCollectionChangedEventArgs args) => tagRegistry?.Refresh(this);

    // Adds, removes and replacements visit only the changed items and moves change nothing: a full resync per add makes
    // building n siblings cubic, and a scan per remove makes clearing them quadratic.
    void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Move:
                return;
            case NotifyCollectionChangedAction.Add when args.NewItems is { } added:
                Adopt(added);
                break;
            case NotifyCollectionChangedAction.Remove when args.OldItems is { } removed:
                Release(removed);
                break;
            case NotifyCollectionChangedAction.Replace when args.NewItems is { } added && args.OldItems is { } removed:
                // Adopting first keeps a node replaced by itself a child throughout.
                Adopt(added);
                Release(removed);
                break;
            default:
                Resync();
                break;
        }
    }

    // Removes this node's entry from its previous parent's children, unless that parent already let it go.
    void LeaveParent(Node previous)
    {
        if (previous.childEntries.ContainsKey(this))
        {
            previous.Children.Remove(this);
        }
    }

    void Adopt(IList added)
    {
        foreach (Node child in added)
        {
            var entries = childEntries.GetValueOrDefault(child) + 1;
            childEntries[child] = entries;
            if (entries == 1)
            {
                child.Parent = this;
            }
        }
    }

    void Release(IList removed)
    {
        foreach (Node child in removed)
        {
            // A node listed twice stays a child until its last entry goes.
            if (!childEntries.TryGetValue(child, out var entries))
            {
                continue;
            }

            if (entries > 1)
            {
                childEntries[child] = entries - 1;
                continue;
            }

            childEntries.Remove(child);
            if (ReferenceEquals(child.Parent, this))
            {
                child.Parent = null;
            }
        }
    }

    void Resync()
    {
        var previous = childEntries;
        childEntries = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        foreach (var child in Children)
        {
            childEntries[child] = childEntries.GetValueOrDefault(child) + 1;
        }

        foreach (var child in previous.Keys)
        {
            if (!childEntries.ContainsKey(child) && ReferenceEquals(child.Parent, this))
            {
                child.Parent = null;
            }
        }

        foreach (var child in childEntries.Keys)
        {
            if (!previous.ContainsKey(child))
            {
                child.Parent = this;
            }
        }
    }
}
