namespace Turian.Engine.Core;

/// <summary>
/// A contiguous index range within a <see cref="Model"/>'s shared index buffer,
/// optionally bound to a material. One submesh maps to one glTF primitive.
/// </summary>
/// <param name="IndexStart">First index (offset within the model's index buffer).</param>
/// <param name="IndexCount">Number of indices to draw for this submesh.</param>
/// <param name="MaterialIndex">
/// Optional source-format material index (e.g. the glTF primitive's <c>material</c> slot).
/// The renderer resolves this to a <see cref="MaterialAsset"/> id via
/// <see cref="AssetIdFactory.Derive(System.Guid, string)"/> with the parent model's id;
/// <c>null</c> falls back to the engine default material.
/// </param>
/// <param name="Bounds">Axis-aligned bounds of the submesh's vertices in model space.</param>
public sealed record SubMesh(uint IndexStart, uint IndexCount, int? MaterialIndex = null, Bounds Bounds = default);
