namespace Turian.Editor.Core;

/// <summary>
/// Keeps the scene tree in step with the open documents: opening an asset loads its scene, saving one
/// writes it back, closing one drops the cached hierarchy. A shell attaches this once instead of
/// re-deriving the wiring.
/// </summary>
public sealed class SceneDocumentBinder(
    AssetManager assets,
    SceneTreeController sceneTree,
    NodeInspectorController inspector) : IDisposable
{
    bool attached;

    /// <summary>Subscribes to the asset manager. Calling it twice does nothing.</summary>
    public void Attach()
    {
        if (attached) return;

        assets.AssetOpened += sceneTree.OpenAsset;
        assets.AssetSaved += sceneTree.SaveAsset;
        assets.AssetClosed += OnAssetClosed;
        attached = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!attached) return;

        assets.AssetOpened -= sceneTree.OpenAsset;
        assets.AssetSaved -= sceneTree.SaveAsset;
        assets.AssetClosed -= OnAssetClosed;
        attached = false;
    }

    void OnAssetClosed(Asset asset)
    {
        sceneTree.CloseAsset(asset.Id);

        // The inspector was showing a node from a hierarchy that no longer exists.
        if (sceneTree.CurrentSceneRoot is null) inspector.ClearSelection();
    }
}
