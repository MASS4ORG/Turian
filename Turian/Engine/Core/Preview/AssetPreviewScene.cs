namespace Turian.Engine.Core;

/// <summary>
/// A throwaway scene built by an <see cref="IScenePreviewProvider"/> to render one asset preview.
/// </summary>
/// <param name="Root">The scene root; its children are rendered (lights, the previewed mesh, etc.).</param>
/// <param name="Bounds">World-space bounds to frame the camera on.</param>
/// <param name="OwnedResources">
/// GPU resources the provider built for this scene (e.g. a procedural mesh), disposed by the caller
/// alongside the scene when the selection changes. <c>null</c> when the provider owns nothing beyond
/// asset-cached content.
/// </param>
public readonly record struct AssetPreviewScene(Node Root, Bounds Bounds, IDisposable? OwnedResources = null);
