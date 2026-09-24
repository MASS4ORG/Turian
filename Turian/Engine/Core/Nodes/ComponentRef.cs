namespace Turian.Engine.Core;

/// <summary>
/// Serializable reference to a <see cref="Component"/> on a node in the current scene,
/// identified by the owning node's GUID.
/// </summary>
/// <typeparam name="T">The component type being referenced.</typeparam>
public class ComponentRef<T> where T : Component
{
    /// <summary>Gets or sets the GUID of the node that owns the referenced component.</summary>
    public Guid NodeId { get; set; }

    /// <summary>Gets a value indicating whether this reference is empty.</summary>
    [JsonIgnore]
    public bool IsEmpty => NodeId == Guid.Empty;

    /// <summary>Initializes an empty reference.</summary>
    public ComponentRef() { }

    /// <summary>Initializes a reference pointing to the component on the given node ID.</summary>
    /// <param name="nodeId">The GUID of the node owning the referenced component.</param>
    public ComponentRef(Guid nodeId) { NodeId = nodeId; }

    /// <summary>
    /// Finds the referenced component in <paramref name="root"/>'s hierarchy.
    /// </summary>
    /// <param name="root">The hierarchy to search — typically the scene root.</param>
    /// <returns>
    /// The referenced component, or <c>null</c> when the reference is empty, the node was not
    /// found, or the node no longer carries a component of type <typeparamref name="T"/>.
    /// </returns>
    public T? Resolve(Node root) => IsEmpty ? null : Node.FindById(root, NodeId)?.GetComponent<T>();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ComponentRef<T> other && NodeId == other.NodeId;

    /// <inheritdoc />
    // The node ID is populated by deserialization, so it must remain mutable.
    // ReSharper disable once NonReadonlyMemberInGetHashCode
    public override int GetHashCode() => HashCode.Combine(typeof(T), NodeId);

    /// <inheritdoc />
    public override string ToString() =>
        IsEmpty ? $"ComponentRef<{typeof(T).Name}>(Empty)" : $"ComponentRef<{typeof(T).Name}>({NodeId})";
}
