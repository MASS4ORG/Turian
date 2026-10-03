namespace Turian.Engine.Core;

public partial class Node
{
    /// <summary>
    /// Finds the node with the given id in <paramref name="root"/>'s hierarchy, including the root
    /// itself, including inactive nodes so stored references can resolve consistently.
    /// </summary>
    /// <param name="root">The hierarchy to search.</param>
    /// <param name="id">The node id to find.</param>
    /// <returns>The matching node, or <c>null</c> when none is found.</returns>
    public static Node? FindById(Node? root, Guid id)
    {
        if (root is null) return null;
        if (root.Id == id) return root;

        foreach (var child in root.Children)
        {
            if (FindById(child, id) is { } found) return found;
        }

        return null;
    }

    /// <summary>
    /// Retrieves an enumerable collection of child nodes of a specific type.
    /// </summary>
    /// <typeparam name="T">The type of child nodes to retrieve.</typeparam>
    /// <returns>An enumerable collection of child nodes of the specified type.</returns>
    public IEnumerable<T> GetChildren<T>()
        where T : Node
    {
        return GetChildren<T>(this);
    }

    /// <summary>
    /// Retrieves an enumerable collection of child nodes of a specific type.
    /// </summary>
    /// <typeparam name="T">The type of child nodes to retrieve.</typeparam>
    /// <param name="node">The parent node.</param>
    /// <returns>An enumerable collection of child nodes of the specified type.</returns>
    public static IEnumerable<T> GetChildren<T>(Node? node)
        where T : Node
    {
        foreach (var child in GetChildren(node))
        {
            if (child is T t)
            {
                yield return t;
            }
        }
    }

    /// <summary>
    /// Retrieves an enumerable collection of child nodes.
    /// </summary>
    /// <param name="node">The parent node.</param>
    /// <returns>An enumerable collection of child nodes of the specified type.</returns>
    public static IEnumerable<Node> GetChildren(Node? node)
    {
        if (node is null)
        {
            yield break;
        }

        // One explicit stack instead of a nested iterator per visited node; children are pushed in reverse so the
        // walk stays depth-first in hierarchy order.
        var pending = new Stack<Node>();
        PushActiveChildren(node, pending);
        while (pending.TryPop(out var current))
        {
            yield return current;
            PushActiveChildren(current, pending);
        }
    }

    // An inactive child hides its whole subtree, as Unity's activeInHierarchy does.
    static void PushActiveChildren(Node node, Stack<Node> pending)
    {
        var children = node.Children;
        for (var i = children.Count - 1; i >= 0; i--)
            if (children[i].IsActive)
                pending.Push(children[i]);
    }

    /// <summary>
    /// Retrieves an enumerable collection of nodes of a specific type from a list of nodes.
    /// </summary>
    /// <typeparam name="T">The type of nodes to retrieve.</typeparam>
    /// <param name="list">The list of nodes to search.</param>
    /// <returns>An enumerable collection of nodes of the specified type.</returns>
    public static IEnumerable<T> GetTypeAndChildren<T>(IEnumerable<Node>? list)
        where T : Node
    {
        if (list is null)
        {
            yield break;
        }

        foreach (var go in list.Where(static node => node.IsActive))
        {
            if (go is T t)
            {
                yield return t;
            }

            foreach (var children in GetChildren<T>(go))
            {
                yield return children;
            }
        }
    }

    /// <summary>
    /// Gets the component of type <typeparamref name="T"/> attached to this node.
    /// </summary>
    /// <typeparam name="T">The type of component to retrieve.</typeparam>
    /// <returns>The component of the specified type, or null if not found.</returns>
    public T? GetComponent<T>()
        where T : Component
    {
        // Indexing avoids the boxed enumerator Collection<T> allocates per foreach; this runs per node in queries.
        var components = Components;
        for (var i = 0; i < components.Count; i++)
        {
            if (components[i] is T componentT)
            {
                return componentT;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the first component assignable to the specified runtime type.
    /// </summary>
    /// <param name="componentType">The component type to look for.</param>
    /// <returns>The matching component, or null if not found.</returns>
    public Component? GetComponent(Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);

        if (!typeof(Component).IsAssignableFrom(componentType))
        {
            throw new ArgumentException("Type must derive from Component.", nameof(componentType));
        }

        var components = Components;
        for (var i = 0; i < components.Count; i++)
        {
            if (componentType.IsInstanceOfType(components[i]))
            {
                return components[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all components of type <typeparamref name="T"/> attached to this node.
    /// </summary>
    /// <typeparam name="T">The component type to retrieve.</typeparam>
    /// <returns>An enumerable of matching components.</returns>
    public IEnumerable<T> GetComponents<T>()
        where T : Component
    {
        foreach (var component in Components)
        {
            if (component is T componentT)
            {
                yield return componentT;
            }
        }
    }

    /// <summary>
    /// Gets all components assignable to the specified runtime type attached to this node.
    /// </summary>
    /// <param name="componentType">The component type to look for.</param>
    /// <returns>An enumerable of matching components.</returns>
    public IEnumerable<Component> GetComponents(Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);

        if (!typeof(Component).IsAssignableFrom(componentType))
        {
            throw new ArgumentException("Type must derive from Component.", nameof(componentType));
        }

        foreach (var component in Components)
        {
            if (componentType.IsInstanceOfType(component))
            {
                yield return component;
            }
        }
    }

}
