namespace Turian.Engine.Core;

/// <summary>
/// Reads an <see cref="InputControl"/> through an <see cref="IInputSource"/>, which is the one place
/// the action layer crosses from a described control to a device query.
/// </summary>
public static class InputSourceExtensions
{
    /// <summary>A button held this far past rest counts as pressed, matching how a trigger is read.</summary>
    public const float PressThreshold = 0.5f;

    /// <summary>The control's current value: 1 or 0 for a button, the axis value otherwise.</summary>
    /// <param name="source">The source to read from.</param>
    /// <param name="control">The control to read.</param>
    /// <param name="playerIndex">The player slot for a gamepad control, or −1 for any.</param>
    public static float Read(this IInputSource source, InputControl control, int playerIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(source);

        return control.Kind switch
        {
            InputControlKind.Key => source.IsKeyDown((Key)control.Code) ? 1f : 0f,
            InputControlKind.MouseButton => source.IsMouseButtonDown((MouseButton)control.Code) ? 1f : 0f,
            InputControlKind.MouseAxis => (MouseAxis)control.Code switch
            {
                MouseAxis.DeltaX => source.MouseDelta.X,
                MouseAxis.DeltaY => source.MouseDelta.Y,
                _ => source.MouseScrollDelta,
            },
            InputControlKind.GamepadButton =>
                source.IsGamepadButtonDown((GamepadButton)control.Code, playerIndex) ? 1f : 0f,
            InputControlKind.GamepadAxis =>
                source.GetGamepadAxis((GamepadAxis)control.Code, playerIndex),
            _ => 0f,
        };
    }

    /// <summary>Whether the control went down this frame, for a rebinding listener.</summary>
    /// <param name="source">The source to read from.</param>
    /// <param name="control">The control to test.</param>
    public static bool WentDown(this IInputSource source, InputControl control)
    {
        ArgumentNullException.ThrowIfNull(source);

        return control.Kind switch
        {
            InputControlKind.Key => source.WasKeyPressed((Key)control.Code),
            InputControlKind.MouseButton => source.WasMouseButtonPressed((MouseButton)control.Code),
            InputControlKind.GamepadButton => source.WasGamepadButtonPressed((GamepadButton)control.Code),
            _ => false,
        };
    }
}
