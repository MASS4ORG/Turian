namespace Turian.Engine.Core;

/// <summary>
/// A single line segment recorded by <see cref="Gizmos"/> for a frame.
/// Endpoints are already transformed into gizmo space (see <see cref="Gizmos.Matrix"/>).
/// </summary>
public readonly record struct GizmoLine(
    Vector3 A,
    Vector3 B,
    Vector4 Color,
    float Thickness);
