namespace Turian.Engine.Core;

/// <summary>
/// Serializes members typed as a <see cref="Node"/>, <see cref="Component"/> or <see cref="DataAsset"/> (or a list or
/// array of them) as references — <c>{"$ref": "&lt;id&gt;"}</c> — instead of inline copies, and resolves them after load.
/// </summary>
/// <remarks>
/// Scene objects are resolved against the hierarchy they were loaded with; DataAssets through an
/// <see cref="IAssetLoader"/>. An id whose target is missing is kept and written back, so saving never drops it.
/// </remarks>
public static class ObjectReferences
{
    /// <summary>Property name of a serialized reference.</summary>
    public const string RefProperty = "$ref";

    static readonly ConditionalWeakTable<IdClass, Dictionary<string, Guid[]>> unresolved = new();

    [ThreadStatic] static int nodeReadDepth;

    /// <summary>
    /// Whether a member of <paramref name="memberType"/> is serialized as a reference.
    /// </summary>
    /// <param name="memberType">The declared member type.</param>
    /// <param name="allowSceneObjects">Whether nodes and components count; only components may reference them.</param>
    /// <returns>True for a referenceable type or a list or array of one.</returns>
    public static bool IsReferenceMember(Type memberType, bool allowSceneObjects) =>
        IsReferenceType(ElementType(memberType) ?? memberType, allowSceneObjects);

    /// <summary>The member ids read from data whose targets are not resolved yet.</summary>
    /// <param name="owner">The object holding the member.</param>
    /// <param name="member">The member name.</param>
    /// <param name="ids">The pending ids; <see cref="Guid.Empty"/> marks an element that needs none.</param>
    /// <returns>True when the member has pending ids.</returns>
    public static bool TryGetUnresolved(IdClass owner, string member, out Guid[] ids)
    {
        if (unresolved.TryGetValue(owner, out var table) && table.TryGetValue(member, out var pending))
        {
            ids = pending;
            return true;
        }

        ids = [];
        return false;
    }

    /// <summary>
    /// Drops pending ids for a member, so a value assigned by the user (including null) is what gets saved.
    /// </summary>
    /// <param name="owner">The object holding the member.</param>
    /// <param name="member">The member name.</param>
    public static void Forget(IdClass owner, string member)
    {
        if (unresolved.TryGetValue(owner, out var table)) table.Remove(member);
    }

    /// <summary>
    /// Resolves pending references on every component in <paramref name="root"/>'s hierarchy.
    /// </summary>
    /// <param name="root">The hierarchy that scene references resolve against.</param>
    /// <param name="loader">Resolves DataAsset references; null leaves them pending.</param>
    public static void Resolve(Node root, IAssetLoader? loader)
    {
        ArgumentNullException.ThrowIfNull(root);

        var objects = new Dictionary<Guid, IdClass>();
        var components = new List<Component>();
        Index(root, objects, components);

        foreach (var component in components)
            ResolveOwner(component, id => objects.GetValueOrDefault(id) ?? LoadData(loader, id));
    }

    /// <summary>Resolves pending DataAsset references held by a DataAsset.</summary>
    /// <param name="data">The DataAsset whose members are resolved.</param>
    /// <param name="loader">Resolves the referenced DataAssets.</param>
    public static void Resolve(DataAsset data, IAssetLoader loader)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(loader);
        ResolveOwner(data, id => LoadData(loader, id));
    }

    internal static void EnterNode() => nodeReadDepth++;

    /// <summary>Leaves a node read; the outermost one resolves the whole hierarchy it produced.</summary>
    internal static void ExitNode(Node? node)
    {
        if (--nodeReadDepth == 0 && node is not null)
            Resolve(node, RuntimeServices.TryGet<IAssetLoader>());
    }

    internal static bool TryWrite(Utf8JsonWriter writer, IdClass owner, string member, Type memberType,
        object? value, bool allowSceneObjects)
    {
        if (!IsReferenceMember(memberType, allowSceneObjects)) return false;

        TryGetUnresolved(owner, member, out var pending);
        writer.WritePropertyName(member);

        if (ElementType(memberType) is null)
        {
            WriteRef(writer, (value as IdClass)?.Id ?? pending.FirstOrDefault());
            return true;
        }

        var items = (value as System.Collections.IList)?.Cast<object?>().ToList() ?? [];
        if (value is null && pending.Length == 0)
        {
            writer.WriteNullValue();
            return true;
        }

        writer.WriteStartArray();
        for (var i = 0; i < Math.Max(items.Count, pending.Length); i++)
        {
            var id = i < items.Count && items[i] is IdClass item ? item.Id : Guid.Empty;
            WriteRef(writer, id != Guid.Empty ? id : i < pending.Length ? pending[i] : Guid.Empty);
        }
        writer.WriteEndArray();
        return true;
    }

    internal static bool TryRead(IdClass owner, string member, Type memberType, JsonElement json,
        bool allowSceneObjects, out object? value)
    {
        value = null;
        if (!IsReferenceMember(memberType, allowSceneObjects)) return false;

        if (ElementType(memberType) is not { } elementType)
        {
            if (!IsRef(json, out var id)) return false;
            Record(owner, member, [id]);
            return true;
        }

        if (json.ValueKind != JsonValueKind.Array) return false;

        var ids = new Guid[json.GetArrayLength()];
        var index = 0;
        foreach (var element in json.EnumerateArray())
        {
            if (!IsRef(element, out ids[index++])) return false;
        }

        value = CreateList(memberType, elementType, ids.Length);
        Record(owner, member, ids);
        return true;
    }

    static void ResolveOwner(IdClass owner, Func<Guid, IdClass?> find)
    {
        if (!unresolved.TryGetValue(owner, out var table) || table.Count == 0) return;

        // Taken out before resolving, so a DataAsset cycle re-entering this owner finds nothing left to do.
        var pending = table.ToList();
        table.Clear();

        foreach (var (member, ids) in pending)
        {
            if (FindMember(owner.GetType(), member) is not { } info) continue;

            var memberType = MemberType(info);
            var elementType = ElementType(memberType);
            var missing = new Guid[ids.Length];
            var anyMissing = false;

            if (elementType is null)
            {
                var target = ids[0] == Guid.Empty ? null : Match(find(ids[0]), memberType);
                if (target is null && ids[0] != Guid.Empty)
                {
                    missing[0] = ids[0];
                    anyMissing = true;
                }
                SetValue(info, owner, target);
            }
            else
            {
                var list = CreateList(memberType, elementType, ids.Length);
                for (var i = 0; i < ids.Length; i++)
                {
                    if (ids[i] == Guid.Empty) continue;
                    list[i] = Match(find(ids[i]), elementType);
                    if (list[i] is not null) continue;
                    missing[i] = ids[i];
                    anyMissing = true;
                }
                SetValue(info, owner, list);
            }

            if (anyMissing) table[member] = missing;
        }
    }

    static object? Match(IdClass? target, Type type) => target is not null && type.IsInstanceOfType(target) ? target : null;

    static IdClass? LoadData(IAssetLoader? loader, Guid id) =>
        loader?.LoadContentAsync<DataAsset>(id).GetAwaiter().GetResult();

    static void Index(Node node, Dictionary<Guid, IdClass> objects, List<Component> components)
    {
        objects.TryAdd(node.Id, node);
        foreach (var component in node.Components)
        {
            objects.TryAdd(component.Id, component);
            components.Add(component);
        }

        foreach (var child in node.Children)
            Index(child, objects, components);
    }

    static void Record(IdClass owner, string member, Guid[] ids)
    {
        if (ids.All(static id => id == Guid.Empty))
        {
            Forget(owner, member);
            return;
        }

        unresolved.GetOrCreateValue(owner)[member] = ids;
    }

    static void WriteRef(Utf8JsonWriter writer, Guid id)
    {
        if (id == Guid.Empty)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString(RefProperty, id);
        writer.WriteEndObject();
    }

    static bool IsRef(JsonElement json, out Guid id)
    {
        id = Guid.Empty;
        if (json.ValueKind == JsonValueKind.Null) return true;

        return json.ValueKind == JsonValueKind.Object
               && json.TryGetProperty(RefProperty, out var value)
               && value.ValueKind == JsonValueKind.String
               && value.TryGetGuid(out id);
    }

    static bool IsReferenceType(Type type, bool allowSceneObjects) =>
        typeof(DataAsset).IsAssignableFrom(type)
        || (allowSceneObjects && (typeof(Node).IsAssignableFrom(type) || typeof(Component).IsAssignableFrom(type)));

    static Type? ElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? type.GetGenericArguments()[0]
            : null;
    }

    static System.Collections.IList CreateList(Type memberType, Type elementType, int count)
    {
        if (memberType.IsArray) return Array.CreateInstance(elementType, count);

        var list = (System.Collections.IList)Activator.CreateInstance(memberType)!;
        for (var i = 0; i < count; i++) list.Add(null);
        return list;
    }

    static MemberInfo? FindMember(Type type, string name) =>
        (MemberInfo?)type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
        ?? type.GetField(name, BindingFlags.Public | BindingFlags.Instance);

    static Type MemberType(MemberInfo member) => member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

    static void SetValue(MemberInfo member, object owner, object? value)
    {
        if (member is PropertyInfo { CanWrite: true } property) property.SetValue(owner, value);
        else if (member is FieldInfo field) field.SetValue(owner, value);
    }
}
