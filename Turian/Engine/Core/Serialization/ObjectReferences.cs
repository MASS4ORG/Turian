namespace Turian.Engine.Core;

/// <summary>
/// Serializes members typed as a <see cref="Node"/>, <see cref="Component"/> or <see cref="DataAsset"/> (or a list
/// or array of them) as references — <c>{"$ref": "&lt;id&gt;"}</c> — instead of inline copies, and resolves them
/// after load.
/// </summary>
/// <remarks>
/// Scene objects are resolved against the loaded scenes; DataAssets through an <see cref="IAssetLoader"/>, batch-loaded
/// in parallel. An id whose target is missing is kept and written back, so saving never drops it, and resolves when
/// its target loads. A destroyed target is written as null. <see cref="SerializeInlineAttribute"/> opts a member out.
/// </remarks>
public static class ObjectReferences
{
    /// <summary>Property name of a serialized reference.</summary>
    public const string RefProperty = "$ref";

    static readonly ConcurrentDictionary<(Type, bool), bool> referenceMembers = new();
    static readonly ConcurrentDictionary<MemberInfo, bool> inlineMembers = new();
    static readonly ConcurrentDictionary<(Type, string), MemberInfo?> members = new();

    [ThreadStatic] static int nodeReadDepth;

    /// <summary>
    /// Whether a member of <paramref name="memberType"/> is serialized as a reference.
    /// </summary>
    /// <param name="memberType">The declared member type.</param>
    /// <param name="allowSceneObjects">Whether nodes and components count; only components may reference them.</param>
    /// <returns>True for a referenceable type or a list or array of one.</returns>
    public static bool IsReferenceMember(Type memberType, bool allowSceneObjects)
    {
        ArgumentNullException.ThrowIfNull(memberType);
        return referenceMembers.GetOrAdd((memberType, allowSceneObjects),
            static key => IsReferenceType(ElementType(key.Item1) ?? key.Item1, key.Item2));
    }

    /// <summary>
    /// Whether <paramref name="member"/> is saved as a reference: a reference member without
    /// <see cref="SerializeInlineAttribute"/>.
    /// </summary>
    /// <param name="member">The field or property.</param>
    /// <param name="memberType">Its declared type.</param>
    /// <param name="allowSceneObjects">Whether nodes and components are references (only on components).</param>
    /// <returns>True when the member is written with <see cref="TryWrite"/>.</returns>
    public static bool IsSavedAsReference(MemberInfo member, Type memberType, bool allowSceneObjects) =>
        IsReferenceMember(memberType, allowSceneObjects)
        && !inlineMembers.GetOrAdd(member, static m => m.GetCustomAttribute<SerializeInlineAttribute>() is not null);

    /// <summary>Whether <paramref name="target"/> is null or a destroyed node or component.</summary>
    /// <param name="target">The referenced object.</param>
    /// <returns>True when the reference should be treated as empty.</returns>
    public static bool IsMissing(object? target) => target switch
    {
        null => true,
        Node node => node.IsDestroyed,
        Component component => component.IsDestroyed,
        _ => false,
    };

    /// <summary>The member ids read from data whose targets are not resolved yet.</summary>
    /// <param name="owner">The object holding the member.</param>
    /// <param name="member">The member name.</param>
    /// <param name="ids">The pending ids; <see cref="Guid.Empty"/> marks an element that needs none.</param>
    /// <returns>True when the member has pending ids.</returns>
    public static bool TryGetUnresolved(IdClass owner, string member, out Guid[] ids)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (owner.PendingReferences is { } table)
        {
            lock (table)
            {
                var index = IndexOf(table, member);
                if (index >= 0)
                {
                    ids = table[index].Value;
                    return true;
                }
            }
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
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.PendingReferences is not { } table) return;

        lock (table)
        {
            var index = IndexOf(table, member);
            if (index >= 0) table.RemoveAt(index);
        }
    }

    /// <summary>Drops the pending id of one element of a list or array member.</summary>
    /// <param name="owner">The object holding the member.</param>
    /// <param name="member">The list or array member name.</param>
    /// <param name="index">The element index.</param>
    public static void Forget(IdClass owner, string member, int index)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.PendingReferences is not { } table) return;

        lock (table)
        {
            var entry = IndexOf(table, member);
            if (entry < 0 || index < 0 || index >= table[entry].Value.Length) return;

            var ids = table[entry].Value;
            ids[index] = Guid.Empty;
            if (ids.All(static id => id == Guid.Empty)) table.RemoveAt(entry);
        }
    }

    /// <summary>
    /// Resolves pending references on every component in <paramref name="root"/>'s hierarchy.
    /// </summary>
    /// <param name="root">The hierarchy that scene references resolve against.</param>
    /// <param name="loader">Resolves DataAsset references; null leaves them pending.</param>
    /// <returns>How many references are still pending.</returns>
    public static int Resolve(Node root, IAssetLoader? loader) => Resolve([root], loader);

    /// <summary>
    /// Resolves pending references on every component in the given hierarchies, against all of them, so a
    /// reference into another loaded scene resolves once that scene is loaded too.
    /// </summary>
    /// <param name="roots">The loaded hierarchies.</param>
    /// <param name="loader">Resolves DataAsset references; null leaves them pending.</param>
    /// <returns>How many references are still pending.</returns>
    public static int Resolve(IEnumerable<Node> roots, IAssetLoader? loader)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var objects = new Dictionary<Guid, IdClass>();
        var components = new List<Component>();
        foreach (var root in roots) Index(root, objects, components);

        components.RemoveAll(static component => component.PendingReferences is null);
        if (components.Count == 0) return 0;

        if (loader is not null)
        {
            var external = components.SelectMany(PendingIds).Where(id => !objects.ContainsKey(id)).Distinct().ToList();
            if (external.Count > 0) loader.PreloadAsync(external).GetAwaiter().GetResult();
        }

        var pending = 0;
        foreach (var component in components)
            pending += ResolveOwner(component, id => objects.GetValueOrDefault(id) ?? CachedData(loader, id));

        return pending;
    }

    /// <summary>
    /// Resolves pending DataAsset references held by a DataAsset, loading the referenced assets in parallel first.
    /// </summary>
    /// <param name="data">The DataAsset whose members are resolved.</param>
    /// <param name="loader">Resolves the referenced DataAssets.</param>
    /// <returns>A task that completes when the references are assigned.</returns>
    public static async Task ResolveAsync(DataAsset data, IAssetLoader loader)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(loader);

        // Taken out before loading, so a DataAsset cycle re-entering this one finds nothing left to do; the
        // other side of a cycle may briefly see this asset before its own references are assigned.
        if (TakePending(data) is not { } pending) return;

        var ids = pending.SelectMany(static entry => entry.Value).Where(static id => id != Guid.Empty).Distinct();
        await loader.PreloadAsync([.. ids]).ConfigureAwait(false);
        Assign(data, pending, id => CachedData(loader, id));
    }

    /// <summary>
    /// Writes a reference member as <c>{"$ref": id}</c> (or an array of them), keeping pending ids for targets that
    /// are not loaded. Returns false, writing nothing, when the member is not a reference member.
    /// </summary>
    /// <param name="writer">The JSON writer, inside the owner's object.</param>
    /// <param name="owner">The object holding the member.</param>
    /// <param name="member">The member name.</param>
    /// <param name="memberType">The declared member type.</param>
    /// <param name="value">The member value.</param>
    /// <param name="allowSceneObjects">Whether nodes and components are references (only on components).</param>
    /// <returns>True when the member was written.</returns>
    public static bool TryWrite(Utf8JsonWriter writer, IdClass owner, string member, Type memberType,
        object? value, bool allowSceneObjects)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(owner);
        if (!IsReferenceMember(memberType, allowSceneObjects)) return false;

        TryGetUnresolved(owner, member, out var pending);
        writer.WritePropertyName(member);

        if (ElementType(memberType) is null)
        {
            WriteRef(writer, IsMissing(value) ? pending.FirstOrDefault() : ((IdClass)value!).Id);
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
            var id = i < items.Count && !IsMissing(items[i]) ? ((IdClass)items[i]!).Id : Guid.Empty;
            WriteRef(writer, id != Guid.Empty ? id : i < pending.Length ? pending[i] : Guid.Empty);
        }
        writer.WriteEndArray();
        return true;
    }

    /// <summary>
    /// Reads a reference member written by <see cref="TryWrite"/>, recording its ids to resolve later. Returns false
    /// for a member that is not a reference member or JSON in another form (an inline value from older files).
    /// </summary>
    /// <param name="owner">The object holding the member.</param>
    /// <param name="member">The member name.</param>
    /// <param name="memberType">The declared member type.</param>
    /// <param name="json">The member's JSON value.</param>
    /// <param name="allowSceneObjects">Whether nodes and components are references (only on components).</param>
    /// <param name="value">The value to assign now: null, or a list of nulls to be filled in.</param>
    /// <returns>True when the member was read.</returns>
    public static bool TryRead(IdClass owner, string member, Type memberType, JsonElement json,
        bool allowSceneObjects, out object? value)
    {
        ArgumentNullException.ThrowIfNull(owner);
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

    internal static void EnterNode() => nodeReadDepth++;

    /// <summary>Leaves a node read; the outermost one resolves the whole hierarchy it produced.</summary>
    internal static void ExitNode(Node? node)
    {
        if (--nodeReadDepth == 0 && node is not null)
            Resolve(node, RuntimeServices.TryGet<IAssetLoader>());
    }

    static IEnumerable<Guid> PendingIds(IdClass owner)
    {
        if (owner.PendingReferences is not { } table) return [];

        lock (table)
        {
            return [.. table.SelectMany(static entry => entry.Value).Where(static id => id != Guid.Empty)];
        }
    }

    static int ResolveOwner(IdClass owner, Func<Guid, IdClass?> find) =>
        TakePending(owner) is { } pending ? Assign(owner, pending, find) : 0;

    static List<KeyValuePair<string, Guid[]>>? TakePending(IdClass owner)
    {
        var pending = Interlocked.Exchange(ref owner.PendingReferences, null);
        return pending is { Count: > 0 } ? pending : null;
    }

    static int Assign(IdClass owner, List<KeyValuePair<string, Guid[]>> pending, Func<Guid, IdClass?> find)
    {
        GeneratedSerializers.TryGet(owner.GetType(), out var generated);
        var stillPending = 0;

        foreach (var (member, ids) in pending)
        {
            var info = generated is null ? FindMember(owner.GetType(), member) : null;
            if ((generated?.MemberType(member) ?? (info is null ? null : MemberType(info))) is not { } memberType)
                continue;

            var elementType = ElementType(memberType);
            var missing = new Guid[ids.Length];
            var anyMissing = false;
            object? value;

            if (elementType is null)
            {
                value = ids[0] == Guid.Empty ? null : Match(find(ids[0]), memberType);
                if (value is null && ids[0] != Guid.Empty)
                {
                    missing[0] = ids[0];
                    anyMissing = true;
                }
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
                value = list;
            }

            if (generated is not null) generated.SetMember(owner, member, value);
            else SetValue(info!, owner, value);

            if (!anyMissing) continue;

            stillPending += missing.Count(static id => id != Guid.Empty);
            var table = PendingTable(owner);
            lock (table)
            {
                if (IndexOf(table, member) < 0) table.Add(new(member, missing));
            }
        }

        return stillPending;
    }

    static object? Match(IdClass? target, Type type) =>
        !IsMissing(target) && type.IsInstanceOfType(target) ? target : null;

    static IdClass? CachedData(IAssetLoader? loader, Guid id) =>
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

        var table = PendingTable(owner);
        lock (table)
        {
            var index = IndexOf(table, member);
            if (index >= 0) table[index] = new(member, ids);
            else table.Add(new(member, ids));
        }
    }

    static List<KeyValuePair<string, Guid[]>> PendingTable(IdClass owner)
    {
        if (owner.PendingReferences is { } table) return table;

        Interlocked.CompareExchange(ref owner.PendingReferences, new List<KeyValuePair<string, Guid[]>>(1), null);
        return owner.PendingReferences!;
    }

    static int IndexOf(List<KeyValuePair<string, Guid[]>> table, string member)
    {
        for (var i = 0; i < table.Count; i++)
        {
            if (string.Equals(table[i].Key, member, StringComparison.Ordinal)) return i;
        }

        return -1;
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
        members.GetOrAdd((type, name), static key =>
            (MemberInfo?)key.Item1.GetProperty(key.Item2, BindingFlags.Public | BindingFlags.Instance)
            ?? key.Item1.GetField(key.Item2, BindingFlags.Public | BindingFlags.Instance));

    static Type MemberType(MemberInfo member) =>
        member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

    static void SetValue(MemberInfo member, object owner, object? value)
    {
        if (member is PropertyInfo { CanWrite: true } property) property.SetValue(owner, value);
        else if (member is FieldInfo field) field.SetValue(owner, value);
    }
}
