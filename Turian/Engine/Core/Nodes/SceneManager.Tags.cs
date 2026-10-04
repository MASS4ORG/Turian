namespace Turian.Engine.Core;

public partial class SceneManager
{
    /// <summary>Finds the first active tagged node in loaded scene order, followed by persistent nodes.</summary>
    public Node? FindWithTag(string? tag)
    {
        foreach (var scene in LoadedScenes)
            if (scene.RootNode.FindWithTag(tag) is { } found) return found;
        return PersistentRoot.FindWithTag(tag);
    }

    /// <summary>Aggregates per-scene tag registries in scene and node registration order.</summary>
    public IReadOnlyList<Node> FindAllWithTag(string? tag) =>
    [
        .. LoadedScenes.SelectMany(scene => scene.RootNode.FindAllWithTag(tag)),
        .. PersistentRoot.FindAllWithTag(tag),
    ];
}
