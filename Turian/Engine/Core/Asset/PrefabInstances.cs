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
    static readonly string[] RootMembers = [nameof(Node.Name), nameof(Node.IsActive), nameof(Node.Transform)];

    static readonly string[] StructuralMembers =
        [ObjectJsonSerializer<IdClass>.TypeIdProperty, idMember, childrenMember, componentsMember, instanceMember];

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

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
        return root.ToJsonString(WriteOptions);
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
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>Finds what every expanded prefab instance in a serialized node hierarchy changes about its prefab.</summary>
    /// <param name="json">A serialized node hierarchy, as <see cref="Serializer.Serialize{T}"/> writes it.</param>
    /// <param name="loadPrefab">Returns a prefab's serialized hierarchy by asset id, or null when it is missing.</param>
    /// <param name="normalize">Rewrites a serialized node hierarchy the way <see cref="Serializer"/> would.</param>
    /// <returns>The overridden members, added objects and missing-prefab instances, by their ids in the hierarchy.</returns>
    public static PrefabInstanceDiff Diff(string json, Func<Guid, string?> loadPrefab,
        Func<string, string>? normalize = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(loadPrefab);

        var diff = new PrefabInstanceDiff();
        if (!json.Contains(instanceMember, StringComparison.Ordinal)) return diff;
        if (JsonNode.Parse(json) is not JsonObject root) return diff;

        DiffTree(root, loadPrefab, normalize ?? NormalizeNode, diff);
        return diff;
    }

    /// <summary>What a new instance of a prefab holds, and which prefab object each of its objects comes from.</summary>
    /// <param name="prefabId">The prefab asset id.</param>
    /// <param name="instanceId">The id of the instance root.</param>
    /// <param name="loadPrefab">Returns a prefab's serialized hierarchy by asset id, or null when it is missing.</param>
    /// <param name="normalize">Rewrites a serialized node hierarchy the way <see cref="Serializer"/> would.</param>
    /// <returns>The expected instance, or null when the prefab is missing.</returns>
    public static PrefabExpectation? Expect(Guid prefabId, Guid instanceId, Func<Guid, string?> loadPrefab,
        Func<string, string>? normalize = null)
    {
        ArgumentNullException.ThrowIfNull(loadPrefab);

        var sourceToDerived = new Dictionary<Guid, Guid>();
        if (Instantiate(prefabId, instanceId, loadPrefab, [], sourceToDerived) is not { } instantiated
            || JsonNode.Parse((normalize ?? NormalizeNode)(instantiated.ToJsonString())) is not JsonObject content)
            return null;

        return new PrefabExpectation(content, sourceToDerived.ToDictionary(pair => pair.Value, pair => pair.Key));
    }

    /// <summary>
    /// The prefab rewritten to match an instance of it: every override, addition and removal becomes the prefab's own.
    /// The prefab keeps its root's id, name, placement and, for a variant, its link to the prefab it varies.
    /// </summary>
    /// <param name="instanceJson">The expanded instance, as <see cref="Serializer.Serialize{T}"/> writes it.</param>
    /// <param name="prefabId">The prefab the instance was made from.</param>
    /// <param name="loadPrefab">Returns a prefab's serialized hierarchy by asset id, or null when it is missing.</param>
    /// <returns>The prefab's new content, or null when the instance or the prefab cannot be read.</returns>
    public static string? ApplyAll(string instanceJson, Guid prefabId, Func<Guid, string?> loadPrefab)
    {
        ArgumentNullException.ThrowIfNull(instanceJson);
        ArgumentNullException.ThrowIfNull(loadPrefab);

        if (JsonNode.Parse(instanceJson) is not JsonObject instance || ReadId(instance) is not { } instanceId
            || loadPrefab(prefabId) is not { } prefabJson || JsonNode.Parse(prefabJson) is not JsonObject prefab
            || Expect(prefabId, instanceId, loadPrefab) is not { } expectation)
            return null;

        RewriteIds(instance, expectation.SourceIds.ToDictionary(pair => pair.Key, pair => pair.Value));
        foreach (var member in RootMembers.Append(idMember))
        {
            if (prefab[member] is { } value) instance[member] = value.DeepClone();
            else instance.Remove(member);
        }

        if (prefab[instanceMember] is { } variantLink) instance[instanceMember] = variantLink.DeepClone();
        else instance.Remove(instanceMember);

        return Compact(instance.ToJsonString(WriteOptions), loadPrefab);
    }

    /// <summary>
    /// Writes one member of a prefab object back to the prefab that owns it. An object a nested prefab provides is
    /// written to that nested prefab, innermost first, and the outer prefabs stop overriding the member.
    /// </summary>
    /// <param name="prefabId">The prefab whose expanded content holds the object.</param>
    /// <param name="objectId">The object's id in that expanded content.</param>
    /// <param name="member">The serialized member name.</param>
    /// <param name="value">The member's new JSON value, with ids in that prefab's terms.</param>
    /// <param name="loadPrefab">Returns a prefab's serialized hierarchy by asset id, or null when it is missing.</param>
    /// <returns>The new content of every prefab that changed, by asset id.</returns>
    public static IReadOnlyDictionary<Guid, string> ApplyMember(Guid prefabId, Guid objectId, string member,
        JsonNode? value, Func<Guid, string?> loadPrefab)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        ArgumentNullException.ThrowIfNull(loadPrefab);

        var changes = new Dictionary<Guid, string>();
        ApplyMember(prefabId, objectId, member, value, loadPrefab, changes, []);
        return changes;
    }

    static void ApplyMember(Guid prefabId, Guid objectId, string member, JsonNode? value,
        Func<Guid, string?> loadPrefab, Dictionary<Guid, string> changes, HashSet<Guid> chain)
    {
        if (!chain.Add(prefabId)
            || (changes.TryGetValue(prefabId, out var pending) ? pending : loadPrefab(prefabId)) is not { } json
            || JsonNode.Parse(json) is not JsonObject prefab)
            return;

        foreach (var owned in OwnObjects(prefab))
        {
            var isInstanceRoot = owned[instanceMember] is JsonObject;
            if (ReadId(owned) != objectId || (isInstanceRoot && !RootMembers.Contains(member))) continue;

            owned[member] = value?.DeepClone();
            changes[prefabId] = prefab.ToJsonString(WriteOptions);
            return;
        }

        foreach (var nested in OwnObjects(prefab).Where(node => node[instanceMember] is JsonObject))
        {
            if (ReadId(nested) is not { } nestedId) continue;

            var link = (JsonObject)nested[instanceMember]!;
            var nestedPrefabId = ReadSourceId(link);
            var sourceToDerived = new Dictionary<Guid, Guid>();
            if (Instantiate(nestedPrefabId, nestedId, loadPrefab, [], sourceToDerived) is null) continue;

            var derivedToSource = sourceToDerived.ToDictionary(pair => pair.Value, pair => pair.Key);
            if (!derivedToSource.TryGetValue(objectId, out var sourceId)) continue;

            if (link[nameof(PrefabInstance.Overrides)] is JsonArray overrides)
            {
                var stale = overrides.OfType<JsonObject>()
                    .Where(entry => entry[nameof(PrefabOverride.Target)]?.GetValue<string>() == sourceId.ToString()
                        && entry[nameof(PrefabOverride.Member)]?.GetValue<string>() == member)
                    .ToList();
                foreach (var entry in stale) overrides.Remove(entry);
                if (overrides.Count == 0) link.Remove(nameof(PrefabInstance.Overrides));
                if (stale.Count > 0) changes[prefabId] = prefab.ToJsonString(WriteOptions);
            }

            var inner = value?.DeepClone();
            RewriteIds(inner, derivedToSource);
            ApplyMember(nestedPrefabId, sourceId, member, inner, loadPrefab, changes, chain);
            return;
        }
    }

    // A compact prefab's own nodes and components: its tree, and what its instances add, but not their prefab content.
    static IEnumerable<JsonObject> OwnObjects(JsonObject node)
    {
        yield return node;

        foreach (var component in Items(node, componentsMember)) yield return component;
        foreach (var child in Items(node, childrenMember).SelectMany(OwnObjects)) yield return child;

        if (node[instanceMember]?[nameof(PrefabInstance.Added)] is not JsonArray added) yield break;
        foreach (var addition in added.OfType<JsonObject>())
        {
            if (addition[nameof(PrefabAddition.Component)] is JsonObject component) yield return component;
            if (addition[nameof(PrefabAddition.Child)] is JsonObject child)
                foreach (var owned in OwnObjects(child)) yield return owned;
        }
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
        // An expanded instance's prefab content was expanded by Instantiate and its additions before attaching.
        if (node[instanceMember] is JsonObject && !node.ContainsKey(childrenMember))
        {
            ExpandInstance(node, loadPrefab, chain);
            return;
        }

        if (node[childrenMember] is not JsonArray children) return;
        foreach (var child in children.OfType<JsonObject>())
            ExpandTree(child, loadPrefab, chain);
    }

    static void ExpandInstance(JsonObject instance, Func<Guid, string?> loadPrefab, HashSet<Guid> chain)
    {
        var link = (JsonObject)instance[instanceMember]!;
        var instanceId = ReadId(instance) ?? Guid.NewGuid();
        var sourceToDerived = new Dictionary<Guid, Guid>();
        var source = Instantiate(ReadSourceId(link), instanceId, loadPrefab, chain, sourceToDerived);
        if (source is null) return;

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

            // Additions belong to the instance, not the prefab, so the prefab may appear again inside them.
            if (addition.Child?.DeepClone() is JsonObject child)
            {
                ExpandTree(child, loadPrefab, chain);
                ArrayOf(parent.Object, childrenMember).Add(child);
            }

            if (addition.Component is not null)
                ArrayOf(parent.Object, componentsMember).Add(addition.Component.DeepClone());
        }

        foreach (var member in RootMembers)
        {
            if (instance[member] is { } value) source[member] = value.DeepClone();
        }

        source[instanceMember] = new JsonObject { [sourceMember] = link[sourceMember]?.DeepClone() };
        ReplaceContent(instance, source);
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
        var detached = Detached(actualIndex, expectedIndex, instanceId);
        var overrides = new JsonArray();
        var added = new JsonArray();
        var removed = new JsonArray();

        foreach (var (id, entry) in actualIndex)
        {
            if (detached.Contains(id)) continue;

            var target = derivedToSource[id].ToString();
            AddOverrides(overrides, target, entry.Object, expectedIndex[id].Object, isRoot: id == instanceId);
            if (!entry.IsNode) continue;

            foreach (var child in Items(entry.Object, childrenMember).Where(child => IsIn(child, detached)))
            {
                CompactTree(child, loadPrefab, normalize);
                added.Add(new JsonObject { ["Parent"] = target, ["Child"] = child.DeepClone() });
            }

            foreach (var component in Items(entry.Object, componentsMember).Where(item => IsIn(item, detached)))
                added.Add(new JsonObject { ["Parent"] = target, ["Component"] = component.DeepClone() });
        }

        foreach (var (id, entry) in expectedIndex)
        {
            if ((!actualIndex.ContainsKey(id) || detached.Contains(id))
                && entry.ParentId is { } parentId && actualIndex.ContainsKey(parentId) && !detached.Contains(parentId))
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
            else if (member is ObjectJsonSerializer<IdClass>.TypeIdProperty or idMember || RootMembers.Contains(member))
                compact[member] = value?.DeepClone();
        }

        ReplaceContent(actual, compact);
    }

    static void DiffTree(JsonObject node, Func<Guid, string?> loadPrefab, Func<string, string> normalize,
        PrefabInstanceDiff diff)
    {
        if (node[instanceMember] is JsonObject && ReadId(node) is { } instanceId)
        {
            if (node.ContainsKey(childrenMember)) DiffInstance(node, instanceId, loadPrefab, normalize, diff);
            else diff.MissingPrefabs.Add(instanceId);
            return;
        }

        foreach (var child in Items(node, childrenMember))
            DiffTree(child, loadPrefab, normalize, diff);
    }

    static void DiffInstance(JsonObject actual, Guid instanceId, Func<Guid, string?> loadPrefab,
        Func<string, string> normalize, PrefabInstanceDiff diff)
    {
        var link = (JsonObject)actual[instanceMember]!;
        if (Instantiate(ReadSourceId(link), instanceId, loadPrefab, [], []) is not { } instantiated
            || JsonNode.Parse(normalize(instantiated.ToJsonString())) is not JsonObject expected)
        {
            diff.MissingPrefabs.Add(instanceId);
            return;
        }

        var expectedIndex = Index(expected);
        var actualIndex = Index(actual);
        var detached = Detached(actualIndex, expectedIndex, instanceId);

        foreach (var (id, entry) in actualIndex)
        {
            if (detached.Contains(id))
            {
                diff.Added.Add(id);
                continue;
            }

            foreach (var member in ChangedMembers(entry.Object, expectedIndex[id].Object, isRoot: id == instanceId))
                diff.Overrides.Add((id, member));

            // Additions belong to the instance, so an instance inside one is compared with its own prefab.
            foreach (var child in Items(entry.Object, childrenMember).Where(child => IsIn(child, detached)))
                DiffTree(child, loadPrefab, normalize, diff);
        }

        foreach (var (id, entry) in expectedIndex)
        {
            if ((!actualIndex.ContainsKey(id) || detached.Contains(id))
                && entry.ParentId is { } parentId && actualIndex.ContainsKey(parentId) && !detached.Contains(parentId))
                diff.Removed.Add(id);
        }
    }

    static void AddOverrides(JsonArray overrides, string target, JsonObject actual, JsonObject expected, bool isRoot)
    {
        foreach (var member in ChangedMembers(actual, expected, isRoot))
        {
            overrides.Add(new JsonObject
            {
                [nameof(PrefabOverride.Target)] = target,
                [nameof(PrefabOverride.Member)] = member,
                [nameof(PrefabOverride.Value)] = actual[member]?.DeepClone(),
            });
        }
    }

    static IEnumerable<string> ChangedMembers(JsonObject actual, JsonObject expected, bool isRoot) =>
        actual
            .Where(pair => !StructuralMembers.Contains(pair.Key) && !(isRoot && RootMembers.Contains(pair.Key)))
            .Where(pair => !expected.ContainsKey(pair.Key) || !JsonNode.DeepEquals(pair.Value, expected[pair.Key]))
            .Select(pair => pair.Key);

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

    // Objects the instance owns rather than the prefab: additions, prefab objects moved to another parent, and
    // everything under them. A moved prefab object is saved as removed plus added, keeping its id.
    static HashSet<Guid> Detached(Dictionary<Guid, Entry> actual, Dictionary<Guid, Entry> expected, Guid rootId)
    {
        return [.. actual.Keys.Where(IsDetached)];

        bool IsDetached(Guid id)
        {
            for (Guid? current = id; current is { } node && node != rootId; current = actual[node].ParentId)
            {
                if (!expected.TryGetValue(node, out var prefabPlace) || prefabPlace.ParentId != actual[node].ParentId)
                    return true;
            }

            return false;
        }
    }

    static bool IsIn(JsonObject item, HashSet<Guid> ids) => ReadId(item) is { } id && ids.Contains(id);

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
