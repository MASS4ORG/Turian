namespace Turian.Engine.Core;

/// <summary>
/// Prefab Node
/// </summary>
[TypeId("a3000000-0000-4000-8000-000000000005")]
public class Prefab : Asset
{
    /// <summary>
    /// Retrieves the content associated with this prefab by resolving it through the scene manager.
    /// Falls back to direct asset-provider loading when runtime services are unavailable.
    /// </summary>
    /// <param name="projectPath">The path of the project where the asset is in.</param>
    /// <returns>An instance of <see cref="Node"/> if the asset content is found; otherwise, null.</returns>
    public Node? GetContent(string projectPath)
    {
        _ = projectPath;

        try
        {
            var sceneManager = RuntimeServices.TryGet<ISceneManager>();
            if (sceneManager is not null)
            {
                return sceneManager.LoadNodeAsync(Id).GetAwaiter().GetResult();
            }

            if (!AssetDatabase.Instance.TryGetAssetProvider(Id, out var provider) || provider is null)
            {
                Log.Logger.LogError(
                    "Prefab asset provider could not be resolved for asset {AssetId} at {RelativePath}",
                    Id,
                    RelativePath);
                return null;
            }

            using var assetStream = provider.GetAssetStream();
            using var memoryStream = new MemoryStream();
            assetStream.CopyTo(memoryStream);

            var jsonString = Encoding.UTF8.GetString(memoryStream.ToArray());
            var node = Serializer.LoadData<Node>(jsonString);
            if (node is null)
            {
                Log.Logger.LogError(
                    "Prefab deserialization returned null for asset {AssetId} at {RelativePath}",
                    Id,
                    RelativePath);
                return null;
            }

            node.Awake(null);
            return node;
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(
                ex,
                "Failed to load prefab content for asset {AssetId} at {RelativePath}",
                Id,
                RelativePath);
            return null;
        }
    }
}
