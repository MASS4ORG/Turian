namespace Turian.Editor.Core;

/// <summary>
/// Deep-clones a <see cref="Node"/> hierarchy, assigning fresh <see cref="System.Guid"/> IDs
/// and wiring parent/child references. Does not touch any UI state.
/// </summary>
public static class NodeCloner
{
    /// <summary>
    /// Deep-copies <paramref name="source"/> — including every component — by round-tripping it
    /// through the polymorphic JSON serializer, then runs <see cref="Node.Awake(Node?)"/> on the copy.
    /// </summary>
    /// <remarks>
    /// Prefer this over <see cref="Clone"/> whenever the copy must be fully independent of the
    /// original: <see cref="Clone"/> reflects over properties and ends up sharing the source's
    /// <see cref="Node.Components"/> instances. Used to rebind scenes after a user-code reload and
    /// to give play mode a throwaway copy of the edited scene.
    /// </remarks>
    /// <param name="source">The hierarchy to copy.</param>
    /// <param name="awake">
    /// Whether to run <see cref="Node.Awake(Node?)"/> on the copy. Pass <c>false</c> when the caller must
    /// finish preparing the world first — play mode registers its services and tracks the scene
    /// before waking components, because <c>OnAwake</c> is where scripts resolve engine services.
    /// </param>
    /// <param name="loader">An explicit play-session loader for direct DataAsset references, when available.</param>
    /// <returns>The independent copy, or <c>null</c> if the round-trip produced no node.</returns>
    public static Node? DeepClone(Node source, bool awake = true, IAssetLoader? loader = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var json = Serializer.Serialize(source);
        var clone = Serializer.LoadData<Node>(json, loader);

        if (awake) clone?.Awake(null);

        return clone;
    }

    /// <summary>
    /// Recursively clones <paramref name="source"/> and all its descendants.
    /// The root clone receives <paramref name="newName"/>; children keep their original names.
    /// </summary>
    public static Node Clone(Node source, string newName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var root = ShallowClone(source, newName);
        foreach (var child in source.Children)
        {
            var clonedChild = Clone(child, child.Name);
            clonedChild.Parent = root;
            root.Children.Add(clonedChild);
        }
        return root;
    }

    /// <summary>
    /// Shallow-clones a single node: copies writable scalar/value-type properties,
    /// skips <c>Children</c>, <c>Parent</c>, and <c>Name</c>.
    /// </summary>
    public static Node ShallowClone(Node source, string newName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = (Node)Activator.CreateInstance(source.GetType())!;
        clone.Name = newName;
        clone.Tags = [.. source.Tags];

        foreach (var prop in source.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true }
                        && p.GetIndexParameters().Length == 0
                        && p.GetMethod?.GetParameters().Length == 0
                        && p.Name is not (nameof(Node.Children) or nameof(Node.Parent) or nameof(Node.Name)
                            or nameof(Node.Tags))))
        {
            object? value;
            try { value = prop.GetValue(source); }
            catch { continue; }

            if (value is null) { prop.SetValue(clone, null); continue; }
            if (prop.PropertyType.IsValueType || prop.PropertyType == typeof(string))
            { prop.SetValue(clone, value); continue; }
            if (value is ICloneable c) { prop.SetValue(clone, c.Clone()); continue; }
            if (TryCloneObject(value, out var cloned)) { prop.SetValue(clone, cloned); continue; }
            prop.SetValue(clone, value);
        }

        return clone;
    }

    static bool TryCloneObject(object value, out object? clone)
    {
        clone = null;
        var type = value.GetType();
        if (type.IsArray) { clone = ((Array)value).Clone(); return true; }

        var method = type.GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null) return false;
        try { clone = method.Invoke(value, null); return clone is not null; }
        catch { return false; }
    }
}
