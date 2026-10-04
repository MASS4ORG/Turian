namespace Turian.Editor.Core;

/// <summary>Interactive transform gizmo mode.</summary>
public enum TransformGizmoMode
{
    /// <summary>Translate (position).</summary>
    Translate,

    /// <summary>Scale.</summary>
    Scale,

    /// <summary>Rotate around an axis using its ring handle.</summary>
    Rotate,

    /// <summary>Translation, rotation and scale handles displayed together.</summary>
    Combined,
}
