namespace Turian.Editor.Core;

/// <summary>Copies selected hierarchies with fresh object ids and references remapped across the copied group.</summary>
public static class NodeDuplication
{
    /// <summary>Creates independent copies while retaining external scene and shared DataAsset references.</summary>
    public static IReadOnlyList<Node> Copy(IReadOnlyList<Node> nodes, IAssetLoader? loader = null,
        Func<Guid, string?>? loadPrefab = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0) return [];
        var ids = new Dictionary<Guid, Guid>();
        foreach (var node in nodes) RegisterIds(node, ids, loadPrefab);
        var container = JsonNode.Parse(Serializer.Serialize(new Node()))!.AsObject();
        container[nameof(Node.Children)] = new JsonArray([.. nodes.Select(node => JsonNode.Parse(Serializer.Serialize(node)))]);
        Remap(container, ids);
        var copied = Serializer.LoadData<Node>(container.ToJsonString(), loader)
            ?? throw new InvalidOperationException("Could not deserialize the copied nodes.");
        var clones = copied.Children.ToArray();
        foreach (var clone in clones) clone.Parent = null;
        var roots = nodes.Select(RootOf).Distinct().Concat(clones);
        ObjectReferences.Resolve(roots, loader);
        return clones;
    }

    static void RegisterIds(Node node, IDictionary<Guid, Guid> ids, Func<Guid, string?>? loadPrefab)
    {
        if (ids.TryAdd(node.Id, Guid.NewGuid())) RegisterPrefabIds(node, ids, loadPrefab);
        foreach (var component in node.Components) ids.TryAdd(component.Id, Guid.NewGuid());
        foreach (var child in node.Children) RegisterIds(child, ids, loadPrefab);
    }

    static void RegisterPrefabIds(Node node, IDictionary<Guid, Guid> ids, Func<Guid, string?>? loadPrefab)
    {
        if (loadPrefab is null || node.PrefabInstance is not { } link) return;
        var expected = PrefabInstances.Expect(link.Source.AssetId, node.Id, loadPrefab);
        if (expected is null) return;
        var instanceId = ids[node.Id];
        foreach (var (objectId, sourceId) in expected.SourceIds)
            ids[objectId] = objectId == node.Id ? instanceId : PrefabInstances.DeriveId(instanceId, sourceId);
    }

    static void Remap(JsonNode? json, IReadOnlyDictionary<Guid, Guid> ids)
    {
        if (json is JsonObject obj)
        {
            foreach (var (name, value) in obj.ToArray())
            {
                if (IsObjectId(name) && Replacement(value, ids) is { } replacement) obj[name] = replacement;
                else Remap(value, ids);
            }
        }
        else if (json is JsonArray array)
            foreach (var value in array) Remap(value, ids);
    }

    static bool IsObjectId(string name) => name is nameof(IdObject.Id) or "$ref";

    static string? Replacement(JsonNode? value, IReadOnlyDictionary<Guid, Guid> ids)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)) return null;
        return Guid.TryParse(text, out var id) && ids.TryGetValue(id, out var replacement)
            ? replacement.ToString() : null;
    }

    static Node RootOf(Node node)
    {
        while (node.Parent is not null) node = node.Parent;
        return node;
    }
}
