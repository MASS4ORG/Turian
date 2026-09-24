namespace Turian.Editor.Core;

/// <summary>
/// Whether the transform gizmo operates in world axes or the selected node's local rotation.
/// Toggled by the toolbar space button (default: world).
/// </summary>
public enum TransformGizmoSpace
{
    /// <summary>Fixed X/Y/Z axes.</summary>
    World,

    /// <summary>Node's rotated orientation.</summary>
    Local,
}
