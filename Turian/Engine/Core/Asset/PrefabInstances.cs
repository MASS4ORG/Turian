using System.Text.Json.Nodes;

namespace Turian.Engine.Core;

/// <summary>
/// Converts prefab instances between their saved form — a <see cref="PrefabInstance"/> link and its differences, with
/// no <c>Children</c> or <c>Components</c> — and the full hierarchy a loaded scene holds.
/// </summary>
/// <remarks>
/// Both directions work on the serialized JSON, so every node and component type round-trips through the regular
/// serializer. Nodes and components copied from a prefab get ids derived from the instance id and their prefab id:
/// stable across loads, unique per instance, so references into an instance survive saving. Nested prefabs expand
/// recursively; a prefab whose root is an instance of another prefab is a variant of it.
/// </remarks>
public static class PrefabInstances
{
    const string instanceMember = nameof(Node.PrefabInstance);
    const string childrenMember = nameof(Node.Children);
    const string componentsMember = nameof(Node.Components);
    const string idMember = nameof(IdClass.Id);
    const string sourceMember = nameof(PrefabInstance.Source);

    // An instance root keeps its own name, activation and placement; they are never overrides.
    static readonly string[] rootMembers = [nameof(Node.Name), nameof(Node.IsActive), nameof(Node.Transform)];

    static readonly string[] structuralMembers =
        [ObjectJsonSerializer<IdClass>.TypeIdProperty, idMember, childrenMember, componentsMember, instanceMember];

    static readonly JsonSerializerOptions writeOptions = new() { WriteIndented = true };

    /// <summary>Replaces every saved prefab instance in a serialized node hierarchy with the prefab's content.</summary>
    /// <param name="json">A serialized node hierarchy.</param>
    /// <param name="loadPrefab">Returns a prefab's serialized hierarchy by asset id, or null when it is missing.</param>
    /// <returns>The hierarchy with its instances expanded. An instance whose prefab is missing is left as saved.</returns>
    public static string Expand(string json, Func<Guid, string?> loadPrefab)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(loadPrefab);

        if (!json.Contains(instanceMember, StringComparison.Ordinal)) return json;
        if (JsonNode.Parse(json) is not JsonObject root) return json;

        ExpandTree(root, loadPrefab, []);
        return root.ToJsonString(writeOptions);
    }

    /// <summary>Reduces every expanded prefab instance in a serialized node hierarchy to its prefab link and differences.</summary>
    /// <param name="json">A serialized node hierarchy, as <see cref="Serializer.Serialize{T}"/> writes it.</param>
    /// <param name="loadPrefab">Returns a prefab's serialized hierarchy by asset id, or null when it is missing.</param>
    /// <param name="normalize">
    /// Rewrites a serialized node hierarchy the way <see cref="Serializer"/> would, so values the prefab file spells
    /// differently are not mistaken for overrides. Defaults to a deserialize/serialize round trip.
    /// </param>
    /// <returns>The hierarchy to save. An instance whose prefab is missing is kept in full.</returns>
    public static string Compact(string json, Func<Guid, string?> loadPrefab, Func<string, string>? normalize = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(loadPrefab);

        if (!json.Contains(instanceMember, StringComparison.Ordinal)) return json;
        if (JsonNode.Parse(json) is not JsonObject root) return json;

        CompactTree(root, loadPrefab, normalize ?? NormalizeNode);
        return root.ToJsonString(writeOptions);
    }

    /// <summary>Reads a prefab's serialized hierarchy from an asset database.</summary>
    /// <param name="database">The database holding the prefab.</param>
    /// <param name="assetId">The prefab asset id.</param>
    /// <returns>The prefab JSON, or null when the database has no such asset.</returns>
    public static string? ReadPrefabJson(AssetDatabase database, Guid assetId)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (!database.TryGetAssetProvider(assetId, out var provider) || provider is null) return null;

        using var stream = provider.GetAssetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The serialized form of a new, unmodified instance of a prefab, for <see cref="Expand"/>.</summary>
    /// <param name="prefabId">The prefab asset id.</param>
    /// <param name="instanceId">The id of the instance root.</param>
    /// <returns>The instance JSON.</returns>
    public static string CreateInstanceJson(Guid prefabId, Guid instanceId) =>
        new JsonObject
        {
            [ObjectJsonSerializer<IdClass>.TypeIdProperty] = TypeRegistry.GetIdOrThrow(typeof(Node)).ToString(),
            [idMember] = instanceId.ToString(),
            [instanceMember] = new JsonObject { [sourceMember] = SourceReference(prefabId) },
        }.ToJsonString();

    /// <summary>The id a prefab object gets inside an instance.</summary>
    /// <param name="instanceId">The id of the instance root.</param>
    /// <param name="prefabObjectId">The node or component id inside the prefab.</param>
    /// <returns>A stable id unique to this instance.</returns>
    public static Guid DeriveId(Guid instanceId, Guid prefabObjectId)
    {
        Span<byte> input = stackalloc byte[32];
        instanceId.TryWriteBytes(input);
        prefabObjectId.TryWriteBytes(input[16..]);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }

    static void ExpandTree(JsonObject node, Func<Guid, string?> loadPrefab, HashSet<Guid> chain)
    {
        var expanded = node[instanceMember] is JsonObject && !node.ContainsKey(childrenMember)
            ? ExpandInstance(node, loadPrefab, chain)
            : null;

        if (node[childrenMember] is not JsonArray children) return;

        // Descendants of an instance must not instantiate its prefab again, or a self-containing prefab never ends.
        var entered = expanded is { } prefabId && chain.Add(prefabId);
        try
        {
            foreach (var child in children.OfType<JsonObject>())
                ExpandTree(child, loadPrefab, chain);
        }
        finally
        {
            if (entered) chain.Remove(expanded!.Value);
        }
    }

    static Guid? ExpandInstance(JsonObject instance, Func<Guid, string?> loadPrefab, HashSet<Guid> chain)
    {
        var link = (JsonObject)instance[instanceMember]!;
        var instanceId = ReadId(instance) ?? Guid.NewGuid();
        var prefabId = ReadSourceId(link);
        var sourceToDerived = new Dictionary<Guid, Guid>();
        var source = Instantiate(prefabId, instanceId, loadPrefab, chain, sourceToDerived);
        if (source is null) return null;

        var data = link.Deserialize<PrefabInstance>(Serializer.JsonOptions) ?? new PrefabInstance();
        var index = Index(source);

        foreach (var removed in data.Removed)
        {
            if (sourceToDerived.TryGetValue(removed, out var id) && index.TryGetValue(id, out var entry))
                entry.Owner?.Remove(entry.Object);
        }

        foreach (var change in data.Overrides)
        {
            if (sourceToDerived.TryGetValue(change.Target, out var id) && index.TryGetValue(id, out var entry))
                entry.Object[change.Member] = change.Value?.DeepClone();
        }

        foreach (var addition in data.Added)
        {
            if (!sourceToDerived.TryGetValue(addition.Parent, out var id)
                || !index.TryGetValue(id, out var parent) || !parent.IsNode)
                continue;

            if (addition.Child is not null) ArrayOf(parent.Object, childrenMember).Add(addition.Child.DeepClone());
            if (addition.Component is not null)
                ArrayOf(parent.Object, componentsMember).Add(addition.Component.DeepClone());
        }

        foreach (var member in rootMembers)
        {
            if (instance[member] is { } value) source[member] = value.DeepClone();
        }

        source[instanceMember] = new JsonObject { [sourceMember] = link[sourceMember]?.DeepClone() };
        ReplaceContent(instance, source);
        return prefabId;
    }

    static JsonObject? Instantiate(
        Guid prefabId,
        Guid instanceId,
        Func<Guid, string?> loadPrefab,
        HashSet<Guid> chain,
        Dictionary<Guid, Guid> sourceToDerived)
    {
        if (prefabId == Guid.Empty) return null;
        if (!chain.Add(prefabId))
        {
            Log.Logger.LogWarning("Prefab {PrefabId} contains an instance of itself; the nested instance is skipped",
                prefabId);
            return null;
        }

        try
        {
            if (loadPrefab(prefabId) is not { } json || JsonNode.Parse(json) is not JsonObject source)
            {
                Log.Logger.LogWarning("Prefab {PrefabId} is missing; its instances keep their saved data", prefabId);
                return null;
            }

            ExpandTree(source, loadPrefab, chain);

            var rootId = ReadId(source);
            foreach (var id in Index(source).Keys)
                sourceToDerived[id] = id == rootId ? instanceId : DeriveId(instanceId, id);

            RewriteIds(source, sourceToDerived);
            return source;
        }
        finally
        {
            chain.Remove(prefabId);
        }
    }

    static void CompactTree(JsonObject node, Func<Guid, string?> loadPrefab, Func<string, string> normalize)
    {
        if (node[instanceMember] is JsonObject && node.ContainsKey(childrenMember))
        {
            CompactInstance(node, loadPrefab, normalize);
            return;
        }

        if (node[childrenMember] is not JsonArray children) return;
        foreach (var child in children.OfType<JsonObject>())
            CompactTree(child, loadPrefab, normalize);
    }

    static void CompactInstance(JsonObject actual, Func<Guid, string?> loadPrefab, Func<string, string> normalize)
    {
        var link = (JsonObject)actual[instanceMember]!;
        if (ReadId(actual) is not { } instanceId) return;

        var sourceToDerived = new Dictionary<Guid, Guid>();
        if (Instantiate(ReadSourceId(link), instanceId, loadPrefab, [], sourceToDerived) is not { } instantiated
            || JsonNode.Parse(normalize(instantiated.ToJsonString())) is not JsonObject expected)
        {
            // Still-unexpanded content of a missing prefab stays in saved form, so it expands once the prefab is back.
            if (!Items(actual, childrenMember).Any() && !Items(actual, componentsMember).Any())
            {
                actual.Remove(childrenMember);
                actual.Remove(componentsMember);
            }

            return;
        }

        var derivedToSource = sourceToDerived.ToDictionary(pair => pair.Value, pair => pair.Key);
        var expectedIndex = Index(expected);
        var actualIndex = Index(actual);
        var overrides = new JsonArray();
        var added = new JsonArray();
        var removed = new JsonArray();

        foreach (var (id, entry) in actualIndex)
        {
            if (!expectedIndex.TryGetValue(id, out var prefabEntry)) continue;

            var target = derivedToSource[id].ToString();
            AddOverrides(overrides, target, entry.Object, prefabEntry.Object, isRoot: id == instanceId);
            if (!entry.IsNode) continue;

            foreach (var child in Items(entry.Object, childrenMember).Where(child => !IsFrom(child, expectedIndex)))
            {
                CompactTree(child, loadPrefab, normalize);
                added.Add(new JsonObject { ["Parent"] = target, ["Child"] = child.DeepClone() });
            }

            foreach (var component in Items(entry.Object, componentsMember)
                         .Where(component => !IsFrom(component, expectedIndex)))
                added.Add(new JsonObject { ["Parent"] = target, ["Component"] = component.DeepClone() });
        }

        foreach (var (id, entry) in expectedIndex)
        {
            if (!actualIndex.ContainsKey(id) && entry.ParentId is { } parentId && actualIndex.ContainsKey(parentId))
                removed.Add(derivedToSource[id].ToString());
        }

        var compactLink = new JsonObject { [sourceMember] = link[sourceMember]?.DeepClone() };
        if (overrides.Count > 0) compactLink[nameof(PrefabInstance.Overrides)] = overrides;
        if (added.Count > 0) compactLink[nameof(PrefabInstance.Added)] = added;
        if (removed.Count > 0) compactLink[nameof(PrefabInstance.Removed)] = removed;

        var compact = new JsonObject();
        foreach (var (member, value) in actual)
        {
            if (member == instanceMember) compact[member] = compactLink;
            else if (member is ObjectJsonSerializer<IdClass>.TypeIdProperty or idMember || rootMembers.Contains(member))
                compact[member] = value?.DeepClone();
        }

        ReplaceContent(actual, compact);
    }

    static void AddOverrides(JsonArray overrides, string target, JsonObject actual, JsonObject expected, bool isRoot)
    {
        foreach (var (member, value) in actual)
        {
            if (structuralMembers.Contains(member) || (isRoot && rootMembers.Contains(member))) continue;
            if (expected.ContainsKey(member) && JsonNode.DeepEquals(value, expected[member])) continue;

            overrides.Add(new JsonObject
            {
                [nameof(PrefabOverride.Target)] = target,
                [nameof(PrefabOverride.Member)] = member,
                [nameof(PrefabOverride.Value)] = value?.DeepClone(),
            });
        }
    }

    static string NormalizeNode(string json) =>
        Serializer.LoadData<Node>(json) is { } node ? Serializer.Serialize(node) : json;

    readonly record struct Entry(JsonObject Object, JsonArray? Owner, Guid? ParentId, bool IsNode);

    // Nodes and their components, keyed by id; nested instance content is included, since it is already expanded.
    static Dictionary<Guid, Entry> Index(JsonObject root)
    {
        var index = new Dictionary<Guid, Entry>();
        Visit(root, null, null);
        return index;

        void Visit(JsonObject node, JsonArray? owner, Guid? parentId)
        {
            var id = ReadId(node);
            if (id is { } nodeId) index[nodeId] = new Entry(node, owner, parentId, IsNode: true);

            if (node[componentsMember] is JsonArray components)
                foreach (var component in components.OfType<JsonObject>())
                    if (ReadId(component) is { } componentId)
                        index[componentId] = new Entry(component, components, id, IsNode: false);

            if (node[childrenMember] is JsonArray children)
                foreach (var child in children.OfType<JsonObject>())
                    Visit(child, children, id);
        }
    }

    static bool IsFrom(JsonObject item, Dictionary<Guid, Entry> prefabIndex) =>
        ReadId(item) is { } id && prefabIndex.ContainsKey(id);

    static IEnumerable<JsonObject> Items(JsonObject node, string member) =>
        node[member] is JsonArray array ? array.OfType<JsonObject>() : [];

    static JsonArray ArrayOf(JsonObject node, string member)
    {
        if (node[member] is JsonArray array) return array;
        array = [];
        node[member] = array;
        return array;
    }

    // Rewrites the prefab's own ids wherever they appear: object ids and references alike.
    static void RewriteIds(JsonNode? node, Dictionary<Guid, Guid> map)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var member in obj.Select(pair => pair.Key).ToList())
                {
                    if (TryMap(obj[member], map, out var mapped)) obj[member] = mapped;
                    else RewriteIds(obj[member], map);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (TryMap(array[i], map, out var mapped)) array[i] = mapped;
                    else RewriteIds(array[i], map);
                }
                break;
        }
    }

    static bool TryMap(JsonNode? node, Dictionary<Guid, Guid> map, out string mapped)
    {
        mapped = string.Empty;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)
            || !Guid.TryParse(text, out var id) || !map.TryGetValue(id, out var target))
            return false;

        mapped = target.ToString();
        return true;
    }

    static void ReplaceContent(JsonObject target, JsonObject source)
    {
        var members = source.ToList();
        source.Clear();
        target.Clear();
        foreach (var (member, value) in members)
            target[member] = value;
    }

    static Guid? ReadId(JsonObject obj) =>
        obj[idMember] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id)
            ? id
            : null;

    static Guid ReadSourceId(JsonObject link) =>
        link[sourceMember]?[nameof(AssetReference<Prefab>.AssetId)] is JsonValue value
        && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id)
            ? id
            : Guid.Empty;

    static JsonObject SourceReference(Guid prefabId) =>
        new() { [nameof(AssetReference<Prefab>.AssetId)] = prefabId.ToString() };
}
