namespace Turian.Editor.Core;

/// <summary>
/// A node's or component's saved state at one moment: the members the serializer writes and, for a node, which
/// children and components it holds. Restoring it puts those values back on the same object.
/// </summary>
/// <remarks>
/// Members holding scene objects or data assets keep the object itself, as the scene does; every other value is kept
/// as a deep copy, so later edits never reach the snapshot.
/// </remarks>
public sealed class ObjectState
{
    static readonly ConcurrentDictionary<Type, MemberInfo[]> MembersByType = new();

    // Written by the structure, not by a member edit.
    static readonly string[] StructuralMembers = [nameof(IdObject.Id), nameof(Node.Children), nameof(Node.Components)];

    readonly (MemberInfo Member, Type Type, string Json, object? Reference)[] values;
    readonly Node[]? children;
    readonly Component[]? components;

    ObjectState((MemberInfo, Type, string, object?)[] values, Node[]? children, Component[]? components)
    {
        this.values = values;
        this.children = children;
        this.components = components;
    }

    /// <summary>Takes the current state of <paramref name="target"/>.</summary>
    /// <param name="target">A node or a component.</param>
    /// <returns>The snapshot.</returns>
    public static ObjectState Capture(IdObject target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var members = MembersOf(target.GetType());
        var values = new (MemberInfo, Type, string, object?)[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            var member = members[i];
            var type = TypeOf(member);
            var value = Get(member, target);
            values[i] = ObjectReferences.IsReferenceMember(type, allowSceneObjects: true)
                ? (member, type, ReferenceKey(value), CopyContainer(value))
                : (member, type, JsonSerializer.Serialize(value, type, Serializer.JsonOptions), null);
        }

        return target is Node node
            ? new ObjectState(values, [.. node.Children], [.. node.Components])
            : new ObjectState(values, null, null);
    }

    /// <summary>
    /// The same state for the object that replaced this one's target when its scene was rebuilt: members are looked
    /// up again on the new type, and every object the state refers to is swapped for its replacement.
    /// </summary>
    /// <param name="targetType">The replacement's type, which may come from a reloaded assembly.</param>
    /// <param name="map">Returns the replacement of an object from before the rebuild.</param>
    /// <returns>The remapped state.</returns>
    public ObjectState Remap(Type targetType, Func<IdObject, IdObject> map)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(map);

        var byName = values.ToDictionary(value => value.Member.Name, StringComparer.Ordinal);
        var remapped = new List<(MemberInfo, Type, string, object?)>();
        foreach (var member in MembersOf(targetType))
        {
            if (!byName.TryGetValue(member.Name, out var old)) continue;

            var type = TypeOf(member);
            if (!ObjectReferences.IsReferenceMember(type, allowSceneObjects: true))
            {
                remapped.Add((member, type, old.Json, null));
                continue;
            }

            var value = MapReferences(old.Reference, type, map);
            remapped.Add((member, type, ReferenceKey(value), value));
        }

        return new ObjectState([.. remapped],
            children?.Select(child => (Node)map(child)).ToArray(),
            components?.Select(component => (Component)map(component)).ToArray());
    }

    static object? MapReferences(object? value, Type type, Func<IdObject, IdObject> map)
    {
        switch (value)
        {
            case IdObject obj:
                return map(obj);
            case IEnumerable items when type.IsArray:
                var mapped = items.Cast<object?>().Select(item => item is IdObject obj ? map(obj) : item).ToArray();
                var array = Array.CreateInstance(type.GetElementType()!, mapped.Length);
                for (var i = 0; i < mapped.Length; i++) array.SetValue(mapped[i], i);
                return array;
            case IEnumerable items when Activator.CreateInstance(type) is IList list:
                foreach (var item in items) list.Add(item is IdObject obj ? map(obj) : item);
                return list;
            default:
                return value;
        }
    }

    /// <summary>Whether both snapshots hold the same values and structure.</summary>
    /// <param name="other">A snapshot of the same object.</param>
    /// <returns>True when nothing differs.</returns>
    public bool SameAs(ObjectState other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (values.Length != other.values.Length) return false;
        for (var i = 0; i < values.Length; i++)
            if (values[i].Json != other.values[i].Json) return false;

        return Same(children, other.children) && Same(components, other.components);
    }

    /// <summary>Puts this snapshot's values and structure back on <paramref name="target"/>.</summary>
    /// <param name="target">The object the snapshot was taken of.</param>
    public void Restore(IdObject target)
    {
        ArgumentNullException.ThrowIfNull(target);

        foreach (var (member, type, json, reference) in values)
        {
            var value = ObjectReferences.IsReferenceMember(type, allowSceneObjects: true)
                ? CopyContainer(reference)
                : JsonSerializer.Deserialize(json, type, Serializer.JsonOptions);
            Set(member, target, value);
        }

        if (target is not Node node) return;
        if (children is not null) RestoreChildren(node, children);
        if (components is not null) RestoreComponents(node, components);
    }

    static void RestoreChildren(Node node, Node[] wanted)
    {
        foreach (var child in node.Children.Where(child => !wanted.Contains(child)).ToList())
        {
            node.Children.Remove(child);
            if (ReferenceEquals(child.Parent, node)) child.Parent = null;
        }

        for (var i = 0; i < wanted.Length; i++)
        {
            var child = wanted[i];
            if (child.Parent is { } other && !ReferenceEquals(other, node)) other.Children.Remove(child);

            var at = node.Children.IndexOf(child);
            if (at == i) continue;
            if (at >= 0) node.Children.RemoveAt(at);
            node.Children.Insert(i, child);
            child.Parent = node;
        }
    }

    static void RestoreComponents(Node node, Component[] wanted)
    {
        foreach (var component in node.Components.Where(component => !wanted.Contains(component)).ToList())
            node.RemoveComponent(component);

        for (var i = 0; i < wanted.Length; i++)
        {
            var component = wanted[i];
            if (!node.Components.Contains(component))
            {
                node.Components.Insert(i, component);
                component.Setup(node);
                continue;
            }

            var at = node.Components.IndexOf(component);
            if (at == i) continue;
            node.Components.RemoveAt(at);
            node.Components.Insert(i, component);
        }
    }

    static bool Same<T>(T[]? left, T[]? right) where T : class =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, ReferenceEqualityComparer.Instance);

    // A list of references is copied so the snapshot keeps its own; the objects in it are shared.
    static object? CopyContainer(object? value) => value switch
    {
        Array array => array.Clone(),
        IList list when value.GetType().IsGenericType => CopyList(list),
        _ => value,
    };

    static IList CopyList(IList list)
    {
        var copy = (IList)Activator.CreateInstance(list.GetType())!;
        foreach (var item in list) copy.Add(item);
        return copy;
    }

    static string ReferenceKey(object? value) => value switch
    {
        null => "null",
        IdObject obj => $"{RuntimeHelpers.GetHashCode(obj)}:{obj.Id}",
        IEnumerable items => string.Join(',', items.Cast<object?>().Select(ReferenceKey)),
        _ => RuntimeHelpers.GetHashCode(value).ToString(CultureInfo.InvariantCulture),
    };

    static object? Get(MemberInfo member, object target) =>
        member is PropertyInfo property ? property.GetValue(target) : ((FieldInfo)member).GetValue(target);

    static void Set(MemberInfo member, object target, object? value)
    {
        if (member is PropertyInfo property) property.SetValue(target, value);
        else ((FieldInfo)member).SetValue(target, value);
    }

    static Type TypeOf(MemberInfo member) =>
        member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;

    // The members the serializer writes for a type, less the ones the structure owns.
    /// <summary>Forgets cached members of types from assemblies released by <see cref="CollectibleAssemblies"/>.</summary>
    internal static void ReleaseCollectible() =>
        CollectibleAssemblies.RemoveReleased(MembersByType, static type => type);

    static MemberInfo[] MembersOf(Type type) => MembersByType.GetOrAdd(type, static type =>
    [
        .. type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0 && property is { CanRead: true, CanWrite: true }
                                                                         && Serialized(property, property.GetGetMethod(nonPublic: true)?.IsPublic == true)),
        .. type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(field => !field.IsInitOnly && Serialized(field, field.IsPublic)),
    ]);

    static bool Serialized(MemberInfo member, bool isPublic) =>
        !StructuralMembers.Contains(member.Name)
        && (isPublic
            ? member.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always }
            : member.GetCustomAttribute<JsonIncludeAttribute>() is not null);
}
