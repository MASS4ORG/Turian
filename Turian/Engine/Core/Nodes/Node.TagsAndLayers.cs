using System.Collections.Specialized;

namespace Turian.Engine.Core;

public partial class Node
{
    TagRegistry? tagRegistry;
    LayerFilter? layerFilter;
    readonly HashSet<Node> knownChildren = [];

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
            if (ReferenceEquals(field, value)) return;
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
        if (ReferenceEquals(tagRegistry, registry)) return;
        tagRegistry?.Unregister(this);
        tagRegistry = registry;
        if (!IsDestroyed) registry?.Register(this);
        foreach (var child in Children) child.BindTagRegistry(registry);
    }

    internal void RefreshActiveHierarchy()
    {
        IsActiveInHierarchy = IsActive && !IsDestroyed && (Parent?.IsActiveInHierarchy ?? true);
        foreach (var child in Children) child.RefreshActiveHierarchy();
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
        if (tagRegistry is not null) return tagRegistry;
        if (Parent is not null) return Parent.EnsureTagRegistry();
        BindTagRegistry(new TagRegistry());
        return tagRegistry!;
    }

    void TagsChanged(object? sender, NotifyCollectionChangedEventArgs args) => tagRegistry?.Refresh(this);

    void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Move) return;
        foreach (var child in knownChildren.Where(child => !Children.Contains(child)).ToArray())
        {
            knownChildren.Remove(child);
            if (ReferenceEquals(child.Parent, this)) child.Parent = null;
        }

        foreach (var child in Children)
        {
            if (!knownChildren.Add(child)) continue;
            child.Parent = this;
        }
    }
}
