namespace Turian.Engine.Core;

/// <summary>
/// Push constants for the gizmo pipeline: the framebuffer size in pixels (used to convert the line width,
/// in pixels, into NDC offsets) and a depth bias applied to the projected Z (used to relax depth testing
/// for the world pass).
/// </summary>
public readonly record struct GizmoPushConstantData(Vector2 ViewportSize, float DepthOffset)
{
    /// <summary>Byte size of the push constant block, matching <c>gizmoShader.vert</c>.</summary>
    public static uint SizeOf() => 12;
}
