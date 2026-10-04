namespace Turian.Engine.Core;

/// <summary>The submeshes submitted and rejected by the current view's CPU frustum test.</summary>
/// <param name="Submitted">Submeshes recorded for drawing.</param>
/// <param name="Culled">Submeshes outside the camera frustum.</param>
public readonly record struct RenderCullingStats(int Submitted, int Culled)
{
    /// <summary>The number of drawable submeshes tested for this frame.</summary>
    public int Total => Submitted + Culled;
}
