namespace Turian.Editor.Core;

/// <summary>
/// Unity's prefab Overrides actions for instances in the open scene: revert a value, a component or everything to the
/// prefab, apply them to the prefab that owns them, and unpack an instance. All of them are undoable; an apply writes
/// the prefab files and refreshes the open instances.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class PrefabOverrideOperations(
    SceneTreeController sceneTree,
    UndoService undo,
    AssetImporter importer,
    AssetDatabase database,
    IAssetLoader loader)
{
    const string revertLabel = "Revert Prefab Override";

    /// <summary>The outermost prefab instance <paramref name="target"/> belongs to, or null.</summary>
    /// <param name="target">A node or component in the open scene.</param>
    /// <returns>The instance root.</returns>
    public static Node? InstanceOf(IdClass target) => NodeOf(target) is { } node
        ? PrefabOverrideTracker.OutermostInstance(node)
        : null;

    /// <summary>What the instance around <paramref name="target"/> changes about its prefab.</summary>
    /// <param name="target">A node or component in the open scene.</param>
    /// <returns>The differences, or null when the target is in no instance.</returns>
    public PrefabInstanceDiff? DiffFor(IdClass target) =>
        InstanceOf(target) is { } root ? PrefabInstances.Diff(Serializer.Serialize(root), LoadPrefab) : null;

    /// <summary>Gives a member back the prefab's value.</summary>
    /// <param name="target">A node or component of an instance.</param>
    /// <param name="member">The serialized member name.</param>
    /// <returns>True when the value was reverted.</returns>
    public bool RevertMember(IdClass target, string member)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (Expectation(target) is not { } expected || Find(expected.Content, target.Id) is not { } prefabObject)
            return false;

        undo.RecordObject(target, revertLabel);
        if (!SetMember(target, member, prefabObject[member])) return false;

        SceneChanged();
        return true;
    }

    /// <summary>Reverts every overridden member of a component, or removes it when the instance added it.</summary>
    /// <param name="component">A component of an instance.</param>
    public void RevertComponent(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (DiffFor(component) is not { } diff) return;
        if (diff.Added.Contains(component.Id) && component.Node is { } owner)
        {
            undo.RecordObject(owner, revertLabel);
            owner.RemoveComponent(component);
            SceneChanged();
            return;
        }

        foreach (var (_, member) in diff.Overrides.Where(entry => entry.Object == component.Id).ToList())
            RevertMember(component, member);
    }

    /// <summary>
    /// Makes an instance match its prefab again: overridden values revert, added objects go, removed ones come back.
    /// </summary>
    /// <param name="instanceRoot">The instance root.</param>
    public void RevertAll(Node instanceRoot)
    {
        ArgumentNullException.ThrowIfNull(instanceRoot);

        if (Expectation(instanceRoot) is not { } expected) return;

        var diff = PrefabInstances.Diff(Serializer.Serialize(instanceRoot), LoadPrefab);

        var objects = ObjectsById(instanceRoot);
        foreach (var (id, member) in diff.Overrides)
        {
            if (!objects.TryGetValue(id, out var target) || Find(expected.Content, id) is not { } prefabObject)
                continue;

            undo.RecordObject(target, revertLabel);
            SetMember(target, member, prefabObject[member]);
        }

        RemoveAdded(instanceRoot, diff.Added);
        RestoreRemoved(instanceRoot, expected.Content);
        SceneChanged();
    }

    /// <summary>Writes a member's value into the prefab that owns the object, innermost prefab first.</summary>
    /// <param name="target">A node or component of an instance.</param>
    /// <param name="member">The serialized member name.</param>
    /// <returns>True when a prefab was written.</returns>
    public bool ApplyMember(IdClass target, string member)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (InstanceOf(target) is not { PrefabInstance: { } link }
            || Expectation(target) is not { } expected
            || !expected.SourceIds.TryGetValue(target.Id, out var sourceId)
            || !TryMemberValue(target, member, expected.SourceIds, out var value))
            return false;

        Write(PrefabInstances.ApplyMember(link.Source.AssetId, sourceId, member, value, LoadPrefab), [target]);
        return true;
    }

    /// <summary>Applies every overridden member of a component to the prefab.</summary>
    /// <param name="component">A component of an instance.</param>
    public void ApplyComponent(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (DiffFor(component) is not { } diff) return;
        foreach (var (_, member) in diff.Overrides.Where(entry => entry.Object == component.Id).ToList())
            ApplyMember(component, member);
    }

    /// <summary>Rewrites the instance's prefab to match the instance, additions and removals included.</summary>
    /// <param name="instanceRoot">The instance root.</param>
    /// <returns>True when the prefab was written.</returns>
    public bool ApplyAll(Node instanceRoot)
    {
        ArgumentNullException.ThrowIfNull(instanceRoot);

        if (instanceRoot.PrefabInstance is not { } link
            || PrefabInstances.ApplyAll(Serializer.Serialize(instanceRoot), link.Source.AssetId, LoadPrefab)
                is not { } content)
            return false;

        Write(new Dictionary<Guid, string> { [link.Source.AssetId] = content }, ObjectsById(instanceRoot).Values);
        return true;
    }

    /// <summary>Breaks an instance's link to its prefab, keeping its hierarchy as plain nodes.</summary>
    /// <param name="instanceRoot">The instance root.</param>
    /// <param name="completely">Also unpacks the prefab instances nested inside it.</param>
    public void Unpack(Node instanceRoot, bool completely)
    {
        ArgumentNullException.ThrowIfNull(instanceRoot);

        foreach (var node in completely ? Descendants(instanceRoot) : [instanceRoot])
        {
            if (node.PrefabInstance is null) continue;
            undo.RecordObject(node, completely ? "Unpack Prefab Completely" : "Unpack Prefab");
            node.PrefabInstance = null;
        }

        SceneChanged();
    }

    // Marks the scene modified and has the scene tree and inspector redraw what changed.
    void SceneChanged()
    {
        sceneTree.MarkAssetModified();
        sceneTree.NotifySelectedNodeUpdated();
    }

    PrefabExpectation? Expectation(IdClass target) =>
        InstanceOf(target) is { PrefabInstance: { } link } root
            ? PrefabInstances.Expect(link.Source.AssetId, root.Id, LoadPrefab)
            : null;

    void RemoveAdded(Node instanceRoot, HashSet<Guid> added)
    {
        foreach (var node in Descendants(instanceRoot).Where(node => !added.Contains(node.Id)).ToList())
        {
            foreach (var child in node.Children.Where(child => added.Contains(child.Id)).ToList())
            {
                undo.RecordObject(node, revertLabel);
                node.Children.Remove(child);
                child.Parent = null;
            }

            foreach (var component in node.Components.Where(component => added.Contains(component.Id)).ToList())
            {
                undo.RecordObject(node, revertLabel);
                node.RemoveComponent(component);
            }
        }
    }

    // Prefab objects the instance removed come back as new objects, under their prefab parent and at their place.
    void RestoreRemoved(Node instanceRoot, JsonObject expected)
    {
        var present = ObjectsById(instanceRoot);
        Walk(expected);

        void Walk(JsonObject prefabNode)
        {
            if (ReadId(prefabNode) is not { } nodeId || !present.TryGetValue(nodeId, out var owner)
                || owner is not Node node)
                return;

            var children = Items(prefabNode, nameof(Node.Children)).ToList();
            for (var i = 0; i < children.Count; i++)
            {
                if (ReadId(children[i]) is { } childId && !present.ContainsKey(childId)
                    && Serializer.LoadData<Node>(children[i].ToJsonString(), loader) is { } restored)
                {
                    undo.RecordObject(node, revertLabel);
                    node.Children.Insert(Math.Min(i, node.Children.Count), restored);
                    restored.Awake(node);
                }
                else
                {
                    Walk(children[i]);
                }
            }

            var components = Items(prefabNode, nameof(Node.Components)).ToList();
            for (var i = 0; i < components.Count; i++)
            {
                if (ReadId(components[i]) is not { } componentId || present.ContainsKey(componentId)
                    || RestoreComponent(components[i]) is not { } component)
                    continue;

                undo.RecordObject(node, revertLabel);
                node.Components.Insert(Math.Min(i, node.Components.Count), component);
                component.Setup(node);
            }
        }
    }

    // A component is read the way a scene reads it, inside a node, then taken out of that node.
    Component? RestoreComponent(JsonObject json)
    {
        var holder = new JsonObject
        {
            [ObjectJsonSerializer<IdClass>.TypeIdProperty] = TypeRegistry.GetIdOrThrow(typeof(Node)).ToString(),
            [nameof(Node.Components)] = new JsonArray(json.DeepClone()),
        };

        if (Serializer.LoadData<Node>(holder.ToJsonString(), loader) is not { } node || node.Components.Count == 0)
            return null;

        var component = node.Components[0];
        node.Components.Clear();
        return component;
    }

    // An apply is one undoable step: undoing writes the prefabs back, and the instance keeps the values it had.
    void Write(IReadOnlyDictionary<Guid, string> changes, IEnumerable<IdClass> instanceObjects)
    {
        var previous = changes.Keys
            .Select(prefabId => (prefabId, Content: PathOf(prefabId) is { } path && File.Exists(path)
                ? File.ReadAllText(path)
                : null))
            .Where(entry => entry.Content is not null)
            .ToDictionary(entry => entry.prefabId, entry => entry.Content!);

        undo.Perform("Apply to Prefab", instanceObjects, () => WriteFiles(changes), () => WriteFiles(previous),
            keepValues: true);
    }

    // All or nothing: when one prefab cannot be written, the ones already written get their content back.
    void WriteFiles(IReadOnlyDictionary<Guid, string> contents)
    {
        var written = new List<(Guid PrefabId, string Path, string? Previous)>();
        try
        {
            foreach (var (prefabId, content) in contents)
            {
                if (PathOf(prefabId) is not { } path) continue;

                var previous = File.Exists(path) ? File.ReadAllText(path) : null;
                written.Add((prefabId, path, previous));
                WriteFile(prefabId, path, content, previous);
            }
        }
        catch
        {
            foreach (var (prefabId, path, previous) in Enumerable.Reverse(written))
            {
                if (previous is null) File.Delete(path);
                else WriteFile(prefabId, path, previous, File.Exists(path) ? File.ReadAllText(path) : null);
            }

            throw;
        }
    }

    void WriteFile(Guid prefabId, string path, string content, string? previous)
    {
        File.WriteAllText(path, content);
        importer.ReimportNow(path);
        sceneTree.RefreshInstances(prefabId, previous);
    }

    string? PathOf(Guid prefabId) =>
        database.TryGetAsset(prefabId, out var record) && record is not null
            ? Path.Combine(record.ProjectRootPath, record.SourceRelativePath)
            : null;

    bool SetMember(IdClass target, string member, JsonNode? json)
    {
        if (MemberNamed(target.GetType(), member) is not { } info) return false;

        var type = info is PropertyInfo property ? property.PropertyType : ((FieldInfo)info).FieldType;
        var value = ObjectReferences.IsReferenceMember(type, allowSceneObjects: true)
            ? ResolveReferences(json, type)
            : json?.Deserialize(type, Serializer.JsonOptions);

        if (info is PropertyInfo writable) writable.SetValue(target, value);
        else ((FieldInfo)info).SetValue(target, value);
        return true;
    }

    // The member's JSON as the prefab would hold it: references by id, translated to the prefab's own ids.
    static bool TryMemberValue(IdClass target, string member, IReadOnlyDictionary<Guid, Guid> sourceIds,
        out JsonNode? json)
    {
        json = null;
        if (MemberNamed(target.GetType(), member) is not { } info) return false;

        var type = info is PropertyInfo property ? property.PropertyType : ((FieldInfo)info).FieldType;
        var value = info is PropertyInfo readable ? readable.GetValue(target) : ((FieldInfo)info).GetValue(target);
        if (!ObjectReferences.IsReferenceMember(type, allowSceneObjects: true))
        {
            json = JsonSerializer.SerializeToNode(value, type, Serializer.JsonOptions);
            return true;
        }

        JsonNode? Reference(object? item) => item is IdClass obj
            ? new JsonObject { [ObjectReferences.RefProperty] = sourceIds.GetValueOrDefault(obj.Id, obj.Id).ToString() }
            : null;

        json = value is IEnumerable items ? new JsonArray([.. items.Cast<object?>().Select(Reference)]) : Reference(value);
        return true;
    }

    object? ResolveReferences(JsonNode? json, Type type)
    {
        if (json is JsonArray array)
        {
            var elementType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            var resolved = array.Select(item => ResolveReference(item)).ToList();
            if (type.IsArray)
            {
                var result = Array.CreateInstance(elementType, resolved.Count);
                for (var i = 0; i < resolved.Count; i++) result.SetValue(resolved[i], i);
                return result;
            }

            var list = (IList)Activator.CreateInstance(type)!;
            foreach (var item in resolved) list.Add(item);
            return list;
        }

        return ResolveReference(json);
    }

    object? ResolveReference(JsonNode? json)
    {
        if (json?[ObjectReferences.RefProperty] is not JsonValue value || !value.TryGetValue<string>(out var text)
            || !Guid.TryParse(text, out var id))
            return null;

        if (sceneTree.EditorSceneRoot is { } root && ObjectsById(root).TryGetValue(id, out var sceneObject))
            return sceneObject;
        return loader.LoadContentAsync<DataAsset>(id).GetAwaiter().GetResult();
    }

    static MemberInfo? MemberNamed(Type type, string name) =>
        (MemberInfo?)type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
        ?? type.GetField(name, BindingFlags.Public | BindingFlags.Instance);

    static Node? NodeOf(IdClass target) => target switch
    {
        Node node => node,
        Component component => component.Node,
        _ => null,
    };

    static Dictionary<Guid, IdClass> ObjectsById(Node root)
    {
        var objects = new Dictionary<Guid, IdClass>();
        foreach (var node in Descendants(root))
        {
            objects.TryAdd(node.Id, node);
            foreach (var component in node.Components) objects.TryAdd(component.Id, component);
        }

        return objects;
    }

    static IEnumerable<Node> Descendants(Node node) => [node, .. node.Children.SelectMany(Descendants)];

    static JsonObject? Find(JsonObject root, Guid id)
    {
        if (ReadId(root) == id) return root;
        foreach (var component in Items(root, nameof(Node.Components)))
            if (ReadId(component) == id) return component;
        return Items(root, nameof(Node.Children)).Select(child => Find(child, id)).FirstOrDefault(found => found is not null);
    }

    static IEnumerable<JsonObject> Items(JsonObject node, string member) =>
        node[member] is JsonArray array ? array.OfType<JsonObject>() : [];

    static Guid? ReadId(JsonObject obj) =>
        obj[nameof(IdClass.Id)] is JsonValue value && value.TryGetValue<string>(out var text)
        && Guid.TryParse(text, out var id)
            ? id
            : null;

    string? LoadPrefab(Guid id) => PrefabInstances.ReadPrefabJson(database, id);
}
