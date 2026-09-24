namespace Turian.Engine.Core;

/// <summary>Which kind of physical control an <see cref="InputControl"/> names.</summary>
public enum InputControlKind
{
    /// <summary>Nothing; an unbound control.</summary>
    None,

    /// <summary>A keyboard key.</summary>
    Key,

    /// <summary>A mouse button.</summary>
    MouseButton,

    /// <summary>A mouse movement or wheel axis, read as a per-frame delta.</summary>
    MouseAxis,

    /// <summary>A gamepad button.</summary>
    GamepadButton,

    /// <summary>A gamepad stick or trigger axis.</summary>
    GamepadAxis,
}

/// <summary>The mouse's continuous controls, reported as the movement accumulated in a frame.</summary>
public enum MouseAxis
{
    /// <summary>Horizontal pointer movement, in pixels.</summary>
    DeltaX,

    /// <summary>Vertical pointer movement, in pixels.</summary>
    DeltaY,

    /// <summary>Scroll-wheel movement, in notches.</summary>
    Scroll,
}

/// <summary>A gamepad's digital controls, named the way a player's hardware labels them.</summary>
public enum GamepadButton
{
    /// <summary>The south face button (A / Cross).</summary>
    South,

    /// <summary>The east face button (B / Circle).</summary>
    East,

    /// <summary>The west face button (X / Square).</summary>
    West,

    /// <summary>The north face button (Y / Triangle).</summary>
    North,

    /// <summary>The left shoulder button.</summary>
    LeftShoulder,

    /// <summary>The right shoulder button.</summary>
    RightShoulder,

    /// <summary>The Back / Select / Share button.</summary>
    Back,

    /// <summary>The Start / Options button.</summary>
    Start,

    /// <summary>Pressing the left stick in.</summary>
    LeftStick,

    /// <summary>Pressing the right stick in.</summary>
    RightStick,

    /// <summary>D-pad up.</summary>
    DPadUp,

    /// <summary>D-pad down.</summary>
    DPadDown,

    /// <summary>D-pad left.</summary>
    DPadLeft,

    /// <summary>D-pad right.</summary>
    DPadRight,
}

/// <summary>A gamepad's continuous controls. Sticks read −1 to 1; triggers read 0 to 1.</summary>
public enum GamepadAxis
{
    /// <summary>Left stick, horizontal.</summary>
    LeftStickX,

    /// <summary>Left stick, vertical. Positive is up.</summary>
    LeftStickY,

    /// <summary>Right stick, horizontal.</summary>
    RightStickX,

    /// <summary>Right stick, vertical. Positive is up.</summary>
    RightStickY,

    /// <summary>Left trigger.</summary>
    LeftTrigger,

    /// <summary>Right trigger.</summary>
    RightTrigger,
}

/// <summary>
/// One physical control on one kind of device — a key, a mouse button, a stick axis. This is the only
/// place in the input stack that knows about hardware; an <see cref="InputAction"/> is defined in
/// terms of these and gameplay code never names one directly.
/// </summary>
/// <param name="Kind">Which sort of control this is.</param>
/// <param name="Code">The control's value inside its kind, cast from the matching enum.</param>
public readonly record struct InputControl(InputControlKind Kind, int Code)
{
    /// <summary>The unbound control, which never reads as pressed and always reads zero.</summary>
    public static InputControl None => default;

    /// <summary>Whether this control reports a continuous value rather than a press.</summary>
    public bool IsAxis => Kind is InputControlKind.MouseAxis or InputControlKind.GamepadAxis;

    /// <summary>Names a keyboard key.</summary>
    /// <param name="key">The key.</param>
    public static InputControl Keyboard(Key key) => new(InputControlKind.Key, (int)key);

    /// <summary>Names a mouse button.</summary>
    /// <param name="button">The button.</param>
    public static InputControl Mouse(MouseButton button) => new(InputControlKind.MouseButton, (int)button);

    /// <summary>Names a mouse movement or wheel axis.</summary>
    /// <param name="axis">The axis.</param>
    public static InputControl MouseMotion(MouseAxis axis) => new(InputControlKind.MouseAxis, (int)axis);

    /// <summary>Names a gamepad button.</summary>
    /// <param name="button">The button.</param>
    public static InputControl Gamepad(GamepadButton button) => new(InputControlKind.GamepadButton, (int)button);

    /// <summary>Names a gamepad stick or trigger axis.</summary>
    /// <param name="axis">The axis.</param>
    public static InputControl GamepadStick(GamepadAxis axis) => new(InputControlKind.GamepadAxis, (int)axis);

    /// <summary>The control as a binding path, e.g. <c>Keyboard/W</c> or <c>Gamepad/LeftStickX</c>.</summary>
    /// <returns>The path, or an empty string for <see cref="None"/>.</returns>
    public override string ToString() => Kind switch
    {
        InputControlKind.Key => $"Keyboard/{(Key)Code}",
        InputControlKind.MouseButton => $"Mouse/{(MouseButton)Code}",
        InputControlKind.MouseAxis => $"Mouse/{(MouseAxis)Code}",
        InputControlKind.GamepadButton => $"Gamepad/{(GamepadButton)Code}",
        InputControlKind.GamepadAxis => $"Gamepad/{(GamepadAxis)Code}",
        _ => string.Empty,
    };

    /// <summary>Reads a binding path written the way <see cref="ToString"/> prints one.</summary>
    /// <param name="path">The path, e.g. <c>Keyboard/W</c>.</param>
    /// <param name="control">The parsed control, or <see cref="None"/> when the path names nothing.</param>
    /// <returns>True when the path was understood.</returns>
    public static bool TryParse(string? path, out InputControl control)
    {
        control = None;
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Split('/', StringSplitOptions.TrimEntries) is not [var device, var name]) return false;

        switch (device.ToUpperInvariant())
        {
            case "KEYBOARD" when Enum.TryParse<Key>(name, ignoreCase: true, out var key):
                control = Keyboard(key);
                return true;

            case "MOUSE" when Enum.TryParse<MouseAxis>(name, ignoreCase: true, out var axis):
                control = MouseMotion(axis);
                return true;

            case "MOUSE" when Enum.TryParse<MouseButton>(name, ignoreCase: true, out var button):
                control = Mouse(button);
                return true;

            case "GAMEPAD" when Enum.TryParse<GamepadAxis>(name, ignoreCase: true, out var stick):
                control = GamepadStick(stick);
                return true;

            case "GAMEPAD" when Enum.TryParse<GamepadButton>(name, ignoreCase: true, out var pad):
                control = Gamepad(pad);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Reads a binding path, returning <see cref="None"/> when it names nothing.</summary>
    /// <param name="path">The path to read.</param>
    public static InputControl Parse(string? path) => TryParse(path, out var control) ? control : None;

    /// <summary>Every control a rebinding operation can capture, in the order it scans them.</summary>
    public static IEnumerable<InputControl> All() =>
        Enum.GetValues<Key>().Where(key => key != Key.Unknown).Select(Keyboard)
            .Concat(Enum.GetValues<MouseButton>().Where(b => b != MouseButton.Unknown).Select(Mouse))
            .Concat(Enum.GetValues<GamepadButton>().Select(Gamepad));
}
