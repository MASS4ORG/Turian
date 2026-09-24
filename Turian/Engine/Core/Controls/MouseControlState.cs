namespace Turian.Engine.Core;

/// <summary>
/// Represents the state of mouse control for a 3D scene.
/// </summary>
public enum MouseControlState
{
    /// <summary>
    /// The mouse control is in an undefined state.
    /// </summary>
    None,

    /// <summary>
    /// The mouse control is in pan mode, allowing the user to pan the view.
    /// </summary>
    Pan,

    /// <summary>
    /// The mouse control is in zoom wheel mode, allowing the user to zoom using the mouse wheel.
    /// </summary>
    ZoomWheel,

    /// <summary>
    /// The mouse control is in rotate mode, allowing the user to rotate the view.
    /// </summary>
    Rotate,

    /// <summary>
    /// The mouse control is in context mode, performing context-specific actions.
    /// </summary>
    Context,

    /// <summary>
    /// The mouse control is in pick mode, allowing the user to pick objects in the scene.
    /// </summary>
    Pick
}
