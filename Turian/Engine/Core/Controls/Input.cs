namespace Turian.Engine.Core;

/// <summary>
/// Static entry point for reading keyboard and mouse state from gameplay code.
///
/// <example>
/// <code>
/// public override void OnUpdate(float deltaTime)
/// {
///     if (Input.IsKeyDown(Key.W))
///         Node.Transform = Node.Transform.Translate(forward * speed * deltaTime);
/// }
/// </code>
/// </example>
/// </summary>
/// <remarks>
/// Resolves an <see cref="IInputSource"/> from <see cref="RuntimeServices"/> on each call, so the
/// same component works unchanged in the standalone runtime and in the Studio's play mode. When no
/// source is registered — for example while the editor is merely previewing a scene — every query
/// returns a neutral value instead of throwing.
/// </remarks>
public static class Input
{
    /// <summary>Gets the active input source, or <c>null</c> when input is not routed.</summary>
    public static IInputSource? Source => RuntimeServices.TryGet<IInputSource>();

    /// <summary>Returns <c>true</c> while <paramref name="key"/> is held down.</summary>
    public static bool IsKeyDown(Key key) => Source?.IsKeyDown(key) ?? false;

    /// <summary>Returns <c>true</c> only during the frame in which <paramref name="key"/> went down.</summary>
    public static bool WasKeyPressed(Key key) => Source?.WasKeyPressed(key) ?? false;

    /// <summary>Returns <c>true</c> only during the frame in which <paramref name="key"/> went up.</summary>
    public static bool WasKeyReleased(Key key) => Source?.WasKeyReleased(key) ?? false;

    /// <summary>Returns <c>true</c> while <paramref name="button"/> is held down.</summary>
    public static bool IsMouseButtonDown(MouseButton button) => Source?.IsMouseButtonDown(button) ?? false;

    /// <summary>Returns <c>true</c> only during the frame in which <paramref name="button"/> went down.</summary>
    public static bool WasMouseButtonPressed(MouseButton button) => Source?.WasMouseButtonPressed(button) ?? false;

    /// <summary>Returns <c>true</c> only during the frame in which <paramref name="button"/> went up.</summary>
    public static bool WasMouseButtonReleased(MouseButton button) => Source?.WasMouseButtonReleased(button) ?? false;

    /// <summary>Gets the pointer position in viewport pixels, with the origin at the top-left.</summary>
    public static Vector2 MousePosition => Source?.MousePosition ?? Vector2.Zero;

    /// <summary>Gets the pointer movement, in pixels, accumulated during the current frame.</summary>
    public static Vector2 MouseDelta => Source?.MouseDelta ?? Vector2.Zero;

    /// <summary>Gets the scroll-wheel movement, in notches, accumulated during the current frame.</summary>
    public static float MouseScrollDelta => Source?.MouseScrollDelta ?? 0f;

    /// <summary>Gets the player slots that currently have a gamepad attached.</summary>
    public static IReadOnlyList<int> ConnectedGamepads => Source?.ConnectedGamepads ?? [];

    /// <summary>Returns <c>true</c> while a gamepad button is held.</summary>
    /// <param name="button">The button to test.</param>
    /// <param name="playerIndex">The player slot, or −1 for any attached gamepad.</param>
    public static bool IsGamepadButtonDown(GamepadButton button, int playerIndex = -1) =>
        Source?.IsGamepadButtonDown(button, playerIndex) ?? false;

    /// <summary>Returns <c>true</c> only during the frame in which a gamepad button went down.</summary>
    /// <param name="button">The button to test.</param>
    /// <param name="playerIndex">The player slot, or −1 for any attached gamepad.</param>
    public static bool WasGamepadButtonPressed(GamepadButton button, int playerIndex = -1) =>
        Source?.WasGamepadButtonPressed(button, playerIndex) ?? false;

    /// <summary>Returns <c>true</c> only during the frame in which a gamepad button went up.</summary>
    /// <param name="button">The button to test.</param>
    /// <param name="playerIndex">The player slot, or −1 for any attached gamepad.</param>
    public static bool WasGamepadButtonReleased(GamepadButton button, int playerIndex = -1) =>
        Source?.WasGamepadButtonReleased(button, playerIndex) ?? false;

    /// <summary>Reads a gamepad axis: −1 to 1 for a stick, 0 to 1 for a trigger.</summary>
    /// <param name="axis">The axis to read.</param>
    /// <param name="playerIndex">The player slot, or −1 for the attached gamepad furthest from rest.</param>
    public static float GetGamepadAxis(GamepadAxis axis, int playerIndex = -1) =>
        Source?.GetGamepadAxis(axis, playerIndex) ?? 0f;

    /// <summary>
    /// Reads one physical control through whatever it is: a press for a button, the current value for
    /// an axis. What an <see cref="InputAction"/> resolves each of its bindings with.
    /// </summary>
    /// <param name="control">The control to read.</param>
    /// <param name="playerIndex">The player slot for a gamepad control, or −1 for any.</param>
    /// <returns>1 or 0 for a button, the axis value otherwise.</returns>
    public static float Read(InputControl control, int playerIndex = -1) =>
        Source is { } source ? source.Read(control, playerIndex) : 0f;
}
