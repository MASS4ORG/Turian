namespace Turian.Editor.Core;

/// <summary>Brings the prefab instances of an open scene up to date after a prefab they depend on is saved.</summary>
public static class PrefabInstanceRefresh
{
    /// <summary>
    /// Rebuilds <paramref name="root"/> with the saved prefab's new content, keeping every instance's overrides: the
    /// scene is reduced to its differences against the prefab as it was, then expanded against the prefab as it is.
    /// </summary>
    /// <param name="root">An open scene's root.</param>
    /// <param name="prefabId">The prefab that was saved.</param>
    /// <param name="previousJson">The prefab's content before the save, or null when it is new.</param>
    /// <param name="loadPrefab">Returns a prefab's current serialized hierarchy by asset id.</param>
    /// <returns>The rebuilt, awakened root, or null when the scene does not change.</returns>
    public static Node? Rebuild(Node root, Guid prefabId, string? previousJson, Func<Guid, string?> loadPrefab)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(loadPrefab);

        var json = Serializer.Serialize(root);
        if (previousJson is null || !json.Contains(nameof(Node.PrefabInstance), StringComparison.Ordinal))
            return null;

        var compact = PrefabInstances.Compact(json, id => id == prefabId ? previousJson : loadPrefab(id));
        var rebuilt = Serializer.LoadData<Node>(PrefabInstances.Expand(compact, loadPrefab));
        if (rebuilt is null || Serializer.Serialize(rebuilt) == json) return null;

        rebuilt.Awake(null);
        return rebuilt;
    }
}
