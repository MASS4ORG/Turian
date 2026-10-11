namespace Turian.Engine.Core;

/// <summary>
/// The render backend's resources as scene components see them. A rendering host registers one with the scene
/// services; a headless host registers none, and components then skip GPU work.
/// </summary>
public interface IRenderDevice
{
    /// <summary>
    /// Returns the model built from a model asset, shared and owned by the backend's cache: callers must not
    /// dispose it.
    /// </summary>
    /// <param name="modelAssetId">The model asset to load.</param>
    /// <param name="assets">The asset database the model is registered in.</param>
    /// <returns>The model, or <c>null</c> when the asset is missing or cannot be loaded.</returns>
    Model? GetModel(Guid modelAssetId, AssetDatabase assets);
}
