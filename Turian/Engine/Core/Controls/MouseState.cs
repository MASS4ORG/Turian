namespace Turian.Engine.Core;

/// <summary>
/// Represents the state of the mouse input including 2D and 3D positions, wheel information,
/// control state, and debugging information.
/// </summary>
public struct MouseState
{
    /// <summary>
    /// Gets or sets the 2D screen position of the mouse.
    /// </summary>
    public Vector2 Pos2D { get; set; }

    /// <summary>
    /// Gets or sets the 2D screen position where dragging started.
    /// </summary>
    public Vector2 StartDrag2D { get; set; }

    /// <summary>
    /// Gets or sets the 3D world position of the mouse.
    /// </summary>
    public Vector3 Pos3D { get; set; }

    /// <summary>
    /// Gets or sets the 3D world position where dragging started.
    /// </summary>
    public Vector3 StartDrag3D { get; set; }

    /// <summary>
    /// Gets or sets the mouse wheel value.
    /// </summary>
    public float Wheel { get; set; }

    /// <summary>
    /// Gets or sets the state of mouse control (e.g., pan, zoom, rotate).
    /// </summary>
    public MouseControlState ControlState { get; set; }
}
