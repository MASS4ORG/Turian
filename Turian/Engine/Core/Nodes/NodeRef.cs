namespace Turian.Engine.Core;

/// <summary>
/// Serializable reference to a <see cref="Node"/> in the current scene, identified by its GUID.
/// </summary>
/// <typeparam name="T">The node type being referenced.</typeparam>
public class NodeRef<T> where T : Node
{
    /// <summary>Gets or sets the GUID of the referenced node.</summary>
    public Guid NodeId { get; set; }

    /// <summary>Gets a value indicating whether this reference is empty.</summary>
    [JsonIgnore]
    public bool IsEmpty => NodeId == Guid.Empty;

    /// <summary>Initializes an empty reference.</summary>
    public NodeRef() { }

    /// <summary>Initializes a reference pointing to the node with the given ID.</summary>
    /// <param name="nodeId">The GUID of the referenced node.</param>
    public NodeRef(Guid nodeId) { NodeId = nodeId; }

    /// <summary>
    /// Finds the referenced node in <paramref name="root"/>'s hierarchy.
    /// </summary>
    /// <param name="root">The hierarchy to search — typically the scene root.</param>
    /// <returns>The referenced node, or <c>null</c> when it is empty or not found.</returns>
    public T? Resolve(Node root) => IsEmpty ? null : Node.FindById(root, NodeId) as T;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NodeRef<T> other && NodeId == other.NodeId;

    /// <inheritdoc />
    // The node ID is populated by deserialization, so it must remain mutable.
    // ReSharper disable once NonReadonlyMemberInGetHashCode
    public override int GetHashCode() => HashCode.Combine(typeof(T), NodeId);

    /// <inheritdoc />
    public override string ToString() =>
        IsEmpty ? $"NodeRef<{typeof(T).Name}>(Empty)" : $"NodeRef<{typeof(T).Name}>({NodeId})";
}
