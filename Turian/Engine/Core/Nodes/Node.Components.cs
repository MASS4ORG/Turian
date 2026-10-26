namespace Turian.Engine.Core;

public partial class Node
{
    /// <summary>
    /// Gets all components of type <typeparamref name="T"/> attached to this node and its children.
    /// </summary>
    /// <typeparam name="T">The component type to retrieve.</typeparam>
    /// <returns>An enumerable of matching components.</returns>
    public IEnumerable<T> GetComponentsInChildren<T>()
        where T : Component
    {
        return GetComponentsInChildren<T>(this);
    }

    /// <summary>
    /// Gets all components assignable to the specified runtime type attached to this node and its children.
    /// </summary>
    /// <param name="componentType">The component type to retrieve.</param>
    /// <returns>An enumerable of matching components.</returns>
    public IEnumerable<Component> GetComponentsInChildren(Type componentType)
    {
        return GetComponentsInChildren(this, componentType);
    }

    /// <summary>
    /// Fills <paramref name="results"/> with the active components of type <typeparamref name="T"/> on this node and
    /// its active descendants, depth-first. Reusing the list keeps per-frame queries allocation-free.
    /// </summary>
    /// <typeparam name="T">The component type to retrieve.</typeparam>
    /// <param name="results">The list to clear and fill.</param>
    public void GetComponentsInChildren<T>(List<T> results)
        where T : Component
    {
        ArgumentNullException.ThrowIfNull(results);
        results.Clear();
        if (!IsActive || IsDestroyed)
        {
            return;
        }

        foreach (var node in EnsureSceneIndex().Nodes(SceneIndex.For<T>()))
        {
            if (!node.IsActiveUnder(this))
            {
                continue;
            }

            foreach (var component in node.Components)
            {
                if (component is T { IsActive: true } match) { results.Add(match); }
            }
        }
    }

    /// <summary>
    /// Gets all components of type <typeparamref name="T"/> attached to the given node and its children.
    /// </summary>
    /// <typeparam name="T">The component type to retrieve.</typeparam>
    /// <param name="node">The root node.</param>
    /// <returns>An enumerable of matching components.</returns>
    public static IEnumerable<T> GetComponentsInChildren<T>(Node? node)
        where T : Component
    {
        if (node is null)
        {
            yield break;
        }

        // One explicit stack instead of a nested iterator per visited node; children are pushed in reverse so the
        // walk stays depth-first in hierarchy order.
        var pending = new Stack<Node>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            // An inactive node hides its whole subtree, as Unity's activeInHierarchy does.
            if (!current.IsActive)
            {
                continue;
            }

            var components = current.Components;
            for (var i = 0; i < components.Count; i++)
            {
                if (components[i] is T { IsActive: true } component)
                {
                    yield return component;
                }
            }

            var children = current.Children;
            for (var i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }
    }

    /// <summary>
    /// Gets all components assignable to the specified runtime type attached to the given node and its children.
    /// </summary>
    /// <param name="node">The root node.</param>
    /// <param name="componentType">The component type to retrieve.</param>
    /// <returns>An enumerable of matching components.</returns>
    public static IEnumerable<Component> GetComponentsInChildren(Node? node, Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);

        if (!typeof(Component).IsAssignableFrom(componentType))
        {
            throw new ArgumentException("Type must derive from Component.", nameof(componentType));
        }

        if (node is null)
        {
            yield break;
        }

        var pending = new Stack<Node>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            if (!current.IsActive)
            {
                continue;
            }

            var components = current.Components;
            for (var i = 0; i < components.Count; i++)
            {
                if (components[i] is { IsActive: true } component && componentType.IsInstanceOfType(component))
                {
                    yield return component;
                }
            }

            var children = current.Children;
            for (var i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }
    }

    /// <summary>
    /// Determines whether this node contains a component of type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The component type to look for.</typeparam>
    /// <returns>True when a matching component exists; otherwise false.</returns>
    public bool HasComponent<T>()
        where T : Component => GetComponent<T>() is not null;

    /// <summary>
    /// Determines whether this node contains a component assignable to the specified runtime type.
    /// </summary>
    /// <param name="componentType">The component type to look for.</param>
    /// <returns>True when a matching component exists; otherwise false.</returns>
    public bool HasComponent(Type componentType) => GetComponent(componentType) is not null;

    /// <summary>
    /// Adds a component to this node.
    /// </summary>
    /// <param name="component">The component to add.</param>
    /// <returns>The added component.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="component"/> is null.</exception>
    public Component AddComponent(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);

        var componentType = component.GetType();
        if (!Component.AllowsMultiple(componentType) && HasComponent(componentType))
        {
            throw new InvalidOperationException(
                $"Node '{Name}' already contains a component of type '{componentType.Name}'.");
        }

        foreach (var requiredType in Component.GetRequiredComponents(componentType))
        {
            if (GetComponent(requiredType) is null)
            {
                if (Activator.CreateInstance(requiredType) is not Component requiredComponent)
                {
                    throw new InvalidOperationException(
                        $"Could not create required component '{requiredType.FullName}' for '{componentType.FullName}'.");
                }

                AddComponent(requiredComponent);
            }
        }

        var currentNode = component.IsAttached ? component.Node : null;
        if (currentNode is not null && !ReferenceEquals(currentNode, this))
        {
            if (!currentNode.RemoveComponent(component))
            {
                throw new InvalidOperationException(
                    $"Could not detach component '{componentType.FullName}' from its current node.");
            }
        }

        //component.Setup(this);

        if (!Components.Contains(component))
        {
            Components.Add(component);
        }

        component.Setup(this); // Awake + Enable

        // Initialize if the scene is already running
        if (Parent is not null || Components.Contains(component))
        {
            // OnInitialize needs vulkan — only call EnsureStarted here;
            // callers that have vulkan should call OnInitialize manually if needed
            component.EnsureStarted();
        }

        return component;
    }

    /// <summary>
    /// Creates and adds a component of type <typeparamref name="T"/> to this node.
    /// </summary>
    /// <typeparam name="T">The component type to create.</typeparam>
    /// <returns>The added component.</returns>
    public T AddComponent<T>()
        where T : Component, new()
    {
        return (T)AddComponent(new T());
    }

    /// <summary>
    /// Removes the specified component from this node.
    /// </summary>
    /// <param name="component">The component to remove.</param>
    /// <returns>True if the component was removed; otherwise false.</returns>
    public bool RemoveComponent(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (!Components.Remove(component))
        {
            return false;
        }

        component.Detach();
        return true;
    }

    /// <summary>
    /// Removes the first component of type <typeparamref name="T"/> from this node.
    /// </summary>
    /// <typeparam name="T">The component type to remove.</typeparam>
    /// <returns>True if a component was removed; otherwise false.</returns>
    public bool RemoveComponent<T>()
        where T : Component
    {
        var component = GetComponent<T>();
        return component is not null && RemoveComponent(component);
    }

    static long transformStamps;
    long transformStamp;

    /// <summary>
    /// A value that changes whenever the global transform of this node may have changed: its own transform, its
    /// parent, or an ancestor's transform. Systems compare it with a stored copy to skip unchanged nodes.
    /// </summary>
    [JsonIgnore, Hide]
    public long TransformVersion
    {
        get
        {
            // Stamps come from one process-wide counter, so the newest stamp on the ancestor chain is new after any
            // change along it.
            var version = transformStamp;
            for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                version = Math.Max(version, ancestor.transformStamp);
            }

            return version;
        }
    }

    /// <summary>
    /// Marks the cached global transform of this node and its descendants as stale.
    /// </summary>
    public void InvalidateGlobalTransformCache()
    {
        isGlobalTransformDirty = true;
        transformStamp = Interlocked.Increment(ref transformStamps);
        // Indexing avoids the boxed enumerator Collection<T> allocates per foreach.
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].MarkGlobalTransformDirty();
        }
    }

    // A dirty node's descendants are already dirty: a cache is only refreshed after its parent's, so stop early.
    void MarkGlobalTransformDirty()
    {
        if (isGlobalTransformDirty)
        {
            return;
        }

        InvalidateGlobalTransformCache();
    }
}
