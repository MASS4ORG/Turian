namespace Turian.Engine.Core;

/// <summary>
/// One world-space UI panel to composite this frame: a rasterized <see cref="Texture"/> drawn on a
/// unit quad (local XY plane, <c>[-0.5, 0.5]</c> on each axis) transformed by <see cref="Model"/>.
/// Produced by the UI layer and consumed by <see cref="WorldUiRenderSystem"/>.
/// </summary>
/// <param name="Texture">The panel's straight-alpha RGBA texture.</param>
/// <param name="Model">World transform placing and sizing the unit quad (same convention as the scene camera matrices).</param>
public readonly record struct WorldUiQuad(Texture Texture, Matrix4x4 Model);

/// <summary>
/// The per-frame context a host passes to the UI layer so it can render — and raycast pointer
/// input against — world-space panels.
/// </summary>
/// <param name="Camera">The camera the scene is being rendered from.</param>
/// <param name="Width">Viewport width in pixels.</param>
/// <param name="Height">Viewport height in pixels.</param>
/// <param name="DeltaTime">Seconds since the previous frame.</param>
public readonly record struct WorldUiFrame(ICamera Camera, uint Width, uint Height, float DeltaTime);
