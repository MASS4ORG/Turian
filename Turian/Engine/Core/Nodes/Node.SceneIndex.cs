namespace Turian.Engine.Core;

public partial class Node
{
    // Weak keys let collectible user-code node types unload.
    static readonly ConditionalWeakTable<Type, StrongBox<bool>> NodeCallbackOverrides = [];

    /// <summary>The index of this node's hierarchy, bound from its root down like the tag registry.</summary>
    internal SceneIndex? BoundIndex { get; private set; }

    /// <summary>The index of this node's hierarchy, created at the root on first use.</summary>
    internal SceneIndex EnsureSceneIndex()
    {
        if (BoundIndex is { } index)
        {
            return index;
        }

        if (Parent is not null)
        {
            return Parent.EnsureSceneIndex();
        }

        BindSceneIndex(new SceneIndex(this));
        return BoundIndex!;
    }

    internal void BindSceneIndex(SceneIndex? index)
    {
        if (ReferenceEquals(BoundIndex, index))
        {
            return;
        }

        BoundIndex?.Unregister(this);
        BoundIndex = index;
        if (!IsDestroyed)
        {
            index?.Register(this);
        }

        foreach (var child in Children)
        {
            child.BindSceneIndex(index);
        }
    }

    /// <summary>Reclassifies this node, e.g. once its components have started.</summary>
    internal void RefreshSceneIndex() => BoundIndex?.Refresh(this);

    internal void ComponentsChanged() => BoundIndex?.Refresh(this);

    internal void ChildrenChanged() => BoundIndex?.OrderChanged();

    /// <summary>Whether this indexed node is active within the requested subtree, ignoring ancestors above it.</summary>
    internal bool IsActiveUnder(Node root)
    {
        if (root.Parent is null) { return IsActiveInHierarchy; }
        for (var current = this; current is not null; current = current.Parent)
        {
            if (!current.IsActive || current.IsDestroyed) { return false; }
            if (ReferenceEquals(current, root)) { return true; }
        }

        return false;
    }

    /// <summary>Whether a node type overrides one of the per-frame callbacks, resolved once per type.</summary>
    internal static bool OverridesFrameCallbacks(Type type)
    {
        if (type == typeof(Node))
        {
            return false;
        }

        return NodeCallbackOverrides.GetValue(type, static candidate => new(
            Overridden(candidate, nameof(OnUpdate)) || Overridden(candidate, nameof(OnLateUpdate)) ||
            Overridden(candidate, nameof(OnFixedUpdate)))).Value;
    }

    static bool Overridden(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, [typeof(float)])?.DeclaringType != typeof(Node);
}
