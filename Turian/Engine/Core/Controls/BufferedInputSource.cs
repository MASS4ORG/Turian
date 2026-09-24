namespace Turian.Engine.Core;

/// <summary>
/// An <see cref="IInputSource"/> that is fed by pushing events into it, tracking held state and
/// per-frame press/release edges.
///
/// <para>
/// The Studio's Game panel pushes translated Avalonia events here; <see cref="SilkInputSource"/>
/// pushes Silk.NET events here. Neither the interface nor gameplay code can tell the difference.
/// </para>
/// </summary>
/// <remarks>
/// Pushes may originate from the UI thread while the game loop reads on the same thread; state is
/// guarded so an out-of-band push cannot corrupt a set mid-enumeration.
/// </remarks>
public sealed class BufferedInputSource : IInputSource
{
    readonly HashSet<Key> keysDown = [];
    readonly HashSet<Key> keysPressed = [];
    readonly HashSet<Key> keysReleased = [];
    readonly HashSet<MouseButton> buttonsDown = [];
    readonly HashSet<MouseButton> buttonsPressed = [];
    readonly HashSet<MouseButton> buttonsReleased = [];
    readonly Dictionary<int, GamepadState> gamepads = [];
    readonly Lock gate = new();

    Vector2 mousePosition;
    Vector2 mouseDelta;
    float scrollDelta;

    /// <inheritdoc/>
    public Vector2 MousePosition { get { lock (gate) return mousePosition; } }

    /// <inheritdoc/>
    public Vector2 MouseDelta { get { lock (gate) return mouseDelta; } }

    /// <inheritdoc/>
    public float MouseScrollDelta { get { lock (gate) return scrollDelta; } }

    /// <inheritdoc/>
    public bool IsKeyDown(Key key) { lock (gate) return keysDown.Contains(key); }

    /// <inheritdoc/>
    public bool WasKeyPressed(Key key) { lock (gate) return keysPressed.Contains(key); }

    /// <inheritdoc/>
    public bool WasKeyReleased(Key key) { lock (gate) return keysReleased.Contains(key); }

    /// <inheritdoc/>
    public bool IsMouseButtonDown(MouseButton button) { lock (gate) return buttonsDown.Contains(button); }

    /// <inheritdoc/>
    public bool WasMouseButtonPressed(MouseButton button) { lock (gate) return buttonsPressed.Contains(button); }

    /// <inheritdoc/>
    public bool WasMouseButtonReleased(MouseButton button) { lock (gate) return buttonsReleased.Contains(button); }

    /// <inheritdoc/>
    public IReadOnlyList<int> ConnectedGamepads { get { lock (gate) return [.. gamepads.Keys.Order()]; } }

    /// <inheritdoc/>
    public bool IsGamepadButtonDown(GamepadButton button, int playerIndex = -1)
    {
        lock (gate) return Pads(playerIndex).Any(pad => pad.Down.Contains(button));
    }

    /// <inheritdoc/>
    public bool WasGamepadButtonPressed(GamepadButton button, int playerIndex = -1)
    {
        lock (gate) return Pads(playerIndex).Any(pad => pad.Pressed.Contains(button));
    }

    /// <inheritdoc/>
    public bool WasGamepadButtonReleased(GamepadButton button, int playerIndex = -1)
    {
        lock (gate) return Pads(playerIndex).Any(pad => pad.Released.Contains(button));
    }

    /// <summary>
    /// The axis value, taking the reading furthest from rest when no player is named — with two pads
    /// attached and one at rest, the one being moved is obviously the one that meant it.
    /// </summary>
    /// <param name="axis">The axis to read.</param>
    /// <param name="playerIndex">The player slot, or −1 for any attached gamepad.</param>
    public float GetGamepadAxis(GamepadAxis axis, int playerIndex = -1)
    {
        lock (gate)
        {
            var value = 0f;
            foreach (var pad in Pads(playerIndex))
                if (pad.Axes.GetValueOrDefault(axis) is var reading && Math.Abs(reading) > Math.Abs(value))
                    value = reading;

            return value;
        }
    }

    /// <inheritdoc/>
    public void NewFrame()
    {
        lock (gate)
        {
            keysPressed.Clear();
            keysReleased.Clear();
            buttonsPressed.Clear();
            buttonsReleased.Clear();
            mouseDelta = Vector2.Zero;
            scrollDelta = 0f;

            foreach (var pad in gamepads.Values)
            {
                pad.Pressed.Clear();
                pad.Released.Clear();
            }
        }
    }

    /// <summary>Records that a gamepad attached to a player slot, so its controls can be read.</summary>
    /// <param name="playerIndex">The slot the gamepad occupies.</param>
    public void PushGamepadConnected(int playerIndex)
    {
        lock (gate) gamepads.TryAdd(playerIndex, new GamepadState());
    }

    /// <summary>Records that a gamepad detached, dropping everything it was holding.</summary>
    /// <param name="playerIndex">The slot the gamepad occupied.</param>
    public void PushGamepadDisconnected(int playerIndex)
    {
        lock (gate) gamepads.Remove(playerIndex);
    }

    /// <summary>Records that a gamepad button went down.</summary>
    /// <param name="playerIndex">The slot the gamepad occupies.</param>
    /// <param name="button">The button.</param>
    public void PushGamepadButtonDown(int playerIndex, GamepadButton button)
    {
        lock (gate)
        {
            var pad = Pad(playerIndex);
            if (pad.Down.Add(button)) pad.Pressed.Add(button);
        }
    }

    /// <summary>Records that a gamepad button went up.</summary>
    /// <param name="playerIndex">The slot the gamepad occupies.</param>
    /// <param name="button">The button.</param>
    public void PushGamepadButtonUp(int playerIndex, GamepadButton button)
    {
        lock (gate)
        {
            var pad = Pad(playerIndex);
            if (pad.Down.Remove(button)) pad.Released.Add(button);
        }
    }

    /// <summary>Records a gamepad axis reading, already past its dead zone.</summary>
    /// <param name="playerIndex">The slot the gamepad occupies.</param>
    /// <param name="axis">The axis.</param>
    /// <param name="value">The reading: −1 to 1 for a stick, 0 to 1 for a trigger.</param>
    public void PushGamepadAxis(int playerIndex, GamepadAxis axis, float value)
    {
        lock (gate) Pad(playerIndex).Axes[axis] = value;
    }

    GamepadState Pad(int playerIndex)
    {
        if (!gamepads.TryGetValue(playerIndex, out var pad)) gamepads[playerIndex] = pad = new GamepadState();
        return pad;
    }

    IEnumerable<GamepadState> Pads(int playerIndex) =>
        playerIndex < 0
            ? gamepads.Values
            : gamepads.TryGetValue(playerIndex, out var pad) ? [pad] : [];

    /// <summary>One attached gamepad's held buttons, this frame's edges and its latest axis readings.</summary>
    sealed class GamepadState
    {
        public HashSet<GamepadButton> Down { get; } = [];

        public HashSet<GamepadButton> Pressed { get; } = [];

        public HashSet<GamepadButton> Released { get; } = [];

        public Dictionary<GamepadAxis, float> Axes { get; } = [];
    }

    /// <summary>Records that <paramref name="key"/> went down. Auto-repeat is ignored.</summary>
    public void PushKeyDown(Key key)
    {
        lock (gate)
        {
            if (!keysDown.Add(key)) return; // key repeat: already held, not a new edge
            keysPressed.Add(key);
        }
    }

    /// <summary>Records that <paramref name="key"/> went up.</summary>
    public void PushKeyUp(Key key)
    {
        lock (gate)
        {
            if (!keysDown.Remove(key)) return;
            keysReleased.Add(key);
        }
    }

    /// <summary>Records that <paramref name="button"/> went down.</summary>
    public void PushMouseDown(MouseButton button)
    {
        lock (gate)
        {
            if (!buttonsDown.Add(button)) return;
            buttonsPressed.Add(button);
        }
    }

    /// <summary>Records that <paramref name="button"/> went up.</summary>
    public void PushMouseUp(MouseButton button)
    {
        lock (gate)
        {
            if (!buttonsDown.Remove(button)) return;
            buttonsReleased.Add(button);
        }
    }

    /// <summary>Records an absolute pointer position, accumulating the movement into the frame delta.</summary>
    public void PushMouseMove(Vector2 position)
    {
        lock (gate)
        {
            mouseDelta += position - mousePosition;
            mousePosition = position;
        }
    }

    /// <summary>Accumulates <paramref name="notches"/> of scroll-wheel movement into the frame delta.</summary>
    public void PushMouseScroll(float notches)
    {
        lock (gate) scrollDelta += notches;
    }

    /// <summary>
    /// Drops all held state and pending edges. Called when the viewport loses focus so keys do not
    /// stay stuck down, and when a play session ends.
    /// </summary>
    public void Clear()
    {
        lock (gate)
        {
            keysDown.Clear();
            keysPressed.Clear();
            keysReleased.Clear();
            buttonsDown.Clear();
            buttonsPressed.Clear();
            buttonsReleased.Clear();
            mouseDelta = Vector2.Zero;
            scrollDelta = 0f;
            gamepads.Clear();
        }
    }
}
