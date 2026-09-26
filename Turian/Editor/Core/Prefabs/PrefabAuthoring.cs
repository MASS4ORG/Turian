using System.Text.Json.Nodes;

namespace Turian.Editor.Core;

/// <summary>
/// Turns scene nodes into prefabs and prefabs into variants, the way Unity's Create Prefab and Create Prefab Variant
/// do: the source node becomes an instance of the new prefab, so later prefab edits reach it.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class PrefabAuthoring(AssetImporter importer)
{
    const string prefabExtension = ".prefab";

    /// <summary>Saves <paramref name="node"/> as a new prefab next to <paramref name="directory"/>'s other assets.</summary>
    /// <param name="node">The node to save; it becomes an instance of the new prefab.</param>
    /// <param name="directory">The folder to write the prefab to.</param>
    /// <returns>The new prefab's path, or null when it could not be imported.</returns>
    public string? CreatePrefab(Node node, string directory)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = UniquePath(directory, node.Name);
        File.WriteAllText(path, Serialize(node, LoadPrefab));
        if (ImportedId(path) is not { } prefabId) return null;

        LinkToPrefab(node, prefabId);
        return path;
    }

    /// <summary>Saves a variant of the prefab at <paramref name="prefabPath"/> beside it.</summary>
    /// <param name="prefabPath">The absolute path of the prefab to derive from.</param>
    /// <returns>The variant's path, or null when the prefab has no asset id yet.</returns>
    public string? CreateVariant(string prefabPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefabPath);

        if (ImportedId(prefabPath) is not { } prefabId) return null;

        var name = $"{Path.GetFileNameWithoutExtension(prefabPath)} Variant";
        var path = UniquePath(Path.GetDirectoryName(prefabPath)!, name);
        File.WriteAllText(path, VariantJson(prefabId, name));
        importer.ReimportNow(path);
        return path;
    }

    /// <summary>
    /// The prefab file for <paramref name="node"/>: its hierarchy with the prefab instances inside kept as links, and
    /// its current ids, so an instance of it derives exactly the ids <see cref="LinkToPrefab"/> gives the node.
    /// </summary>
    /// <param name="node">The node to save.</param>
    /// <param name="loadPrefab">Returns a prefab's serialized hierarchy by asset id, or null when it is missing.</param>
    /// <returns>The prefab JSON.</returns>
    public static string Serialize(Node node, Func<Guid, string?> loadPrefab)
    {
        ArgumentNullException.ThrowIfNull(node);

        return PrefabInstances.Compact(Serializer.Serialize(node), loadPrefab);
    }

    /// <summary>
    /// Makes <paramref name="root"/> an instance of the prefab it was saved as. Objects keep their identity, so
    /// references to them hold; only their ids change, to the ones loading the instance would give them.
    /// </summary>
    /// <param name="root">The node saved by <see cref="Serialize"/>.</param>
    /// <param name="prefabId">The new prefab's asset id.</param>
    public static void LinkToPrefab(Node root, Guid prefabId)
    {
        ArgumentNullException.ThrowIfNull(root);

        var instanceId = Guid.NewGuid();
        Relink(root, isRoot: true);
        root.PrefabInstance = new PrefabInstance { Source = new AssetReference<Prefab>(prefabId) };

        void Relink(Node node, bool isRoot)
        {
            node.Id = isRoot ? instanceId : PrefabInstances.DeriveId(instanceId, node.Id);
            foreach (var component in node.Components)
                component.Id = PrefabInstances.DeriveId(instanceId, component.Id);
            foreach (var child in node.Children)
                Relink(child, isRoot: false);
        }
    }

    /// <summary>A variant's file: an unmodified instance of the prefab, under its own name.</summary>
    /// <param name="prefabId">The prefab the variant derives from.</param>
    /// <param name="name">The variant root's name.</param>
    /// <returns>The variant JSON.</returns>
    public static string VariantJson(Guid prefabId, string name)
    {
        var variant = (JsonObject)JsonNode.Parse(PrefabInstances.CreateInstanceJson(prefabId, Guid.NewGuid()))!;
        variant[nameof(Node.Name)] = name;
        return variant.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static string? LoadPrefab(Guid id) => PrefabInstances.ReadPrefabJson(AssetDatabase.Instance, id);

    Guid? ImportedId(string path)
    {
        importer.ReimportNow(path, overwriteExisting: false);
        return Asset.Load($"{path}.meta")?.Id;
    }

    static string UniquePath(string directory, string name)
    {
        var stem = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        if (stem.Length == 0) stem = "Prefab";

        var path = Path.Combine(directory, stem + prefabExtension);
        for (var n = 1; File.Exists(path); n++)
            path = Path.Combine(directory, $"{stem} {n}{prefabExtension}");
        return path;
    }
}
