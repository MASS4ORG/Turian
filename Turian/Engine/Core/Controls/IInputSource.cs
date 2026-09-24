namespace Turian.Engine.Core;

/// <summary>
/// Supplies keyboard and mouse state to gameplay code, decoupled from where the events come from.
///
/// <para>
/// The standalone runtime feeds this from Silk.NET (<see cref="SilkInputSource"/>); the Studio's
/// in-editor play mode feeds it from Avalonia events raised by the Game panel. Gameplay code should
/// use the static <see cref="Input"/> facade rather than resolving this interface directly.
/// </para>
/// </summary>
/// <remarks>
/// Implementations are driven from the game-loop thread. <see cref="NewFrame"/> is called once per
/// tick by <see cref="SceneTicker"/>, after all update callbacks have run.
/// </remarks>
public interface IInputSource
{
    /// <summary>Returns <c>true</c> while <paramref name="key"/> is held down.</summary>
    bool IsKeyDown(Key key);

    /// <summary>Returns <c>true</c> only during the frame in which <paramref name="key"/> went down.</summary>
    bool WasKeyPressed(Key key);

    /// <summary>Returns <c>true</c> only during the frame in which <paramref name="key"/> went up.</summary>
    bool WasKeyReleased(Key key);

    /// <summary>Returns <c>true</c> while <paramref name="button"/> is held down.</summary>
    bool IsMouseButtonDown(MouseButton button);

    /// <summary>Returns <c>true</c> only during the frame in which <paramref name="button"/> went down.</summary>
    bool WasMouseButtonPressed(MouseButton button);

    /// <summary>Returns <c>true</c> only during the frame in which <paramref name="button"/> went up.</summary>
    bool WasMouseButtonReleased(MouseButton button);

    /// <summary>Gets the pointer position in viewport pixels, with the origin at the top-left.</summary>
    Vector2 MousePosition { get; }

    /// <summary>Gets the pointer movement, in pixels, accumulated during the current frame.</summary>
    Vector2 MouseDelta { get; }

    /// <summary>Gets the scroll-wheel movement, in notches, accumulated during the current frame.</summary>
    float MouseScrollDelta { get; }

    /// <summary>
    /// Ends the current frame: clears the per-frame edges reported by <see cref="WasKeyPressed"/>,
    /// <see cref="WasKeyReleased"/> and their mouse counterparts, and resets the accumulated deltas.
    /// </summary>
    void NewFrame();

    /// <summary>
    /// Player slots with a gamepad attached. A source with no gamepad support reports none, which is
    /// what the Studio's play mode does today.
    /// </summary>
    IReadOnlyList<int> ConnectedGamepads => [];

    /// <summary>Returns <c>true</c> while a gamepad button is held.</summary>
    /// <param name="button">The button to test.</param>
    /// <param name="playerIndex">The player slot, or −1 for any attached gamepad.</param>
    bool IsGamepadButtonDown(GamepadButton button, int playerIndex = -1) => false;

    /// <summary>Returns <c>true</c> only during the frame in which a gamepad button went down.</summary>
    /// <param name="button">The button to test.</param>
    /// <param name="playerIndex">The player slot, or −1 for any attached gamepad.</param>
    bool WasGamepadButtonPressed(GamepadButton button, int playerIndex = -1) => false;

    /// <summary>Returns <c>true</c> only during the frame in which a gamepad button went up.</summary>
    /// <param name="button">The button to test.</param>
    /// <param name="playerIndex">The player slot, or −1 for any attached gamepad.</param>
    bool WasGamepadButtonReleased(GamepadButton button, int playerIndex = -1) => false;

    /// <summary>
    /// Reads a gamepad axis: −1 to 1 for a stick, 0 to 1 for a trigger, already past its dead zone.
    /// </summary>
    /// <param name="axis">The axis to read.</param>
    /// <param name="playerIndex">The player slot, or −1 for the attached gamepad furthest from rest.</param>
    float GetGamepadAxis(GamepadAxis axis, int playerIndex = -1) => 0f;
}
