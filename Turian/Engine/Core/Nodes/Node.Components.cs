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

        if (node.IsActive)
        {
            foreach (var component in node.GetComponents<T>())
            {
                if (component.IsActive)
                {
                    yield return component;
                }
            }
        }

        foreach (var child in node.Children)
        {
            foreach (var component in GetComponentsInChildren<T>(child))
            {
                yield return component;
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

        if (node.IsActive)
        {
            foreach (var component in node.GetComponents(componentType))
            {
                if (component.IsActive)
                {
                    yield return component;
                }
            }
        }

        foreach (var child in node.Children)
        {
            foreach (var component in GetComponentsInChildren(child, componentType))
            {
                yield return component;
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

    /// <summary>
    /// Calculates and returns the global transformation of the node, taking into account its parent's transformation.
    /// </summary>
    /// <returns>The global transformation of the node.</returns>
    Transform CalculateGlobalTransform()
    {
        // If this node has a parent, combine its transformation with its own.
        return Parent is not null ? Transform.AddParent(Parent.GlobalTransform) : Transform;
    }

    /// <summary>
    /// Invalidates the cached global transform of the node and invokes the global transform invalidated event.
    /// </summary>
    public void InvalidateGlobalTransformCache()
    {
        lock (lockObject)
        {
            // Reset the cached global transform to null.
            cachedGlobalTransform = null;

            // Invoke the global transform invalidated event, if any subscribers.
            OnGlobalTransformInvalidated?.Invoke();
        }
    }
}
