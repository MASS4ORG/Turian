namespace Turian.Engine.Core;

/// <summary>
/// Routes Silk.NET keyboard and mouse events into a <see cref="BufferedInputSource"/> so that the
/// standalone runtime exposes the same <see cref="Input"/> surface as the Studio's play mode.
/// </summary>
/// <param name="inputManager">The runtime input manager owning the Silk.NET input context.</param>
public sealed class SilkInputSource(InputManager inputManager) : IInputSource, IDisposable
{
    readonly BufferedInputSource state = new();
    readonly IInputContext input =
        (inputManager ?? throw new ArgumentNullException(nameof(inputManager))).Input;

    bool subscribed;

    /// <summary>Subscribes to the connected keyboards and mice. Safe to call more than once.</summary>
    public void Initialize()
    {
        if (subscribed) return;
        subscribed = true;

        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
        }

        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnScroll;
        }

        foreach (var gamepad in input.Gamepads)
        {
            gamepad.ButtonDown += OnPadDown;
            gamepad.ButtonUp += OnPadUp;
            if (gamepad.IsConnected) state.PushGamepadConnected(gamepad.Index);
        }

        input.ConnectionChanged += OnConnectionChanged;
    }

    /// <inheritdoc/>
    public bool IsKeyDown(Key key) => state.IsKeyDown(key);

    /// <inheritdoc/>
    public bool WasKeyPressed(Key key) => state.WasKeyPressed(key);

    /// <inheritdoc/>
    public bool WasKeyReleased(Key key) => state.WasKeyReleased(key);

    /// <inheritdoc/>
    public bool IsMouseButtonDown(MouseButton button) => state.IsMouseButtonDown(button);

    /// <inheritdoc/>
    public bool WasMouseButtonPressed(MouseButton button) => state.WasMouseButtonPressed(button);

    /// <inheritdoc/>
    public bool WasMouseButtonReleased(MouseButton button) => state.WasMouseButtonReleased(button);

    /// <inheritdoc/>
    public Vector2 MousePosition => state.MousePosition;

    /// <inheritdoc/>
    public Vector2 MouseDelta => state.MouseDelta;

    /// <inheritdoc/>
    public float MouseScrollDelta => state.MouseScrollDelta;

    /// <inheritdoc/>
    public IReadOnlyList<int> ConnectedGamepads => state.ConnectedGamepads;

    /// <inheritdoc/>
    public bool IsGamepadButtonDown(GamepadButton button, int playerIndex = -1) =>
        state.IsGamepadButtonDown(button, playerIndex);

    /// <inheritdoc/>
    public bool WasGamepadButtonPressed(GamepadButton button, int playerIndex = -1) =>
        state.WasGamepadButtonPressed(button, playerIndex);

    /// <inheritdoc/>
    public bool WasGamepadButtonReleased(GamepadButton button, int playerIndex = -1) =>
        state.WasGamepadButtonReleased(button, playerIndex);

    /// <inheritdoc/>
    public float GetGamepadAxis(GamepadAxis axis, int playerIndex = -1) =>
        state.GetGamepadAxis(axis, playerIndex);

    /// <summary>
    /// Samples the sticks and triggers, which Silk reports as state rather than as events, and then
    /// rolls the frame. Called once per tick by <see cref="SceneTicker"/>.
    /// </summary>
    public void NewFrame()
    {
        foreach (var gamepad in input.Gamepads.Where(pad => pad.IsConnected)) PollAxes(gamepad);

        state.NewFrame();
    }

    /// <summary>Readings below this count as rest, which is what keeps a worn stick from drifting.</summary>
    const float deadZone = 0.15f;

    void PollAxes(IGamepad gamepad)
    {
        foreach (var stick in gamepad.Thumbsticks)
        {
            var (x, y) = stick.Index == 0
                ? (GamepadAxis.LeftStickX, GamepadAxis.LeftStickY)
                : (GamepadAxis.RightStickX, GamepadAxis.RightStickY);

            state.PushGamepadAxis(gamepad.Index, x, Deaden(stick.X));

            // Silk reports the stick's Y growing downwards; every axis here is positive-up so that a
            // stick and a W key can bind to the same action without one of them being inverted.
            state.PushGamepadAxis(gamepad.Index, y, Deaden(-stick.Y));
        }

        foreach (var trigger in gamepad.Triggers)
            state.PushGamepadAxis(gamepad.Index,
                trigger.Index == 0 ? GamepadAxis.LeftTrigger : GamepadAxis.RightTrigger,
                Deaden(trigger.Position));
    }

    static float Deaden(float value) => Math.Abs(value) < deadZone ? 0f : value;

    /// <summary>Maps Silk's button names onto the engine's, which are named by position rather than letter.</summary>
    static GamepadButton? Map(ButtonName button) => button switch
    {
        ButtonName.A => GamepadButton.South,
        ButtonName.B => GamepadButton.East,
        ButtonName.X => GamepadButton.West,
        ButtonName.Y => GamepadButton.North,
        ButtonName.LeftBumper => GamepadButton.LeftShoulder,
        ButtonName.RightBumper => GamepadButton.RightShoulder,
        ButtonName.Back => GamepadButton.Back,
        ButtonName.Start => GamepadButton.Start,
        ButtonName.LeftStick => GamepadButton.LeftStick,
        ButtonName.RightStick => GamepadButton.RightStick,
        ButtonName.DPadUp => GamepadButton.DPadUp,
        ButtonName.DPadDown => GamepadButton.DPadDown,
        ButtonName.DPadLeft => GamepadButton.DPadLeft,
        ButtonName.DPadRight => GamepadButton.DPadRight,
        _ => null,
    };

    void OnConnectionChanged(IInputDevice device, bool connected)
    {
        if (device is not IGamepad gamepad) return;

        if (connected)
        {
            gamepad.ButtonDown += OnPadDown;
            gamepad.ButtonUp += OnPadUp;
            state.PushGamepadConnected(gamepad.Index);
            return;
        }

        gamepad.ButtonDown -= OnPadDown;
        gamepad.ButtonUp -= OnPadUp;
        state.PushGamepadDisconnected(gamepad.Index);
    }

    void OnPadDown(IGamepad gamepad, Button button)
    {
        if (Map(button.Name) is { } mapped) state.PushGamepadButtonDown(gamepad.Index, mapped);
    }

    void OnPadUp(IGamepad gamepad, Button button)
    {
        if (Map(button.Name) is { } mapped) state.PushGamepadButtonUp(gamepad.Index, mapped);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!subscribed) return;
        subscribed = false;

        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown -= OnKeyDown;
            keyboard.KeyUp -= OnKeyUp;
        }

        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown -= OnMouseDown;
            mouse.MouseUp -= OnMouseUp;
            mouse.MouseMove -= OnMouseMove;
            mouse.Scroll -= OnScroll;
        }

        foreach (var gamepad in input.Gamepads)
        {
            gamepad.ButtonDown -= OnPadDown;
            gamepad.ButtonUp -= OnPadUp;
        }

        input.ConnectionChanged -= OnConnectionChanged;
        state.Clear();
    }

    void OnKeyDown(IKeyboard _, Key key, int __) => state.PushKeyDown(key);

    void OnKeyUp(IKeyboard _, Key key, int __) => state.PushKeyUp(key);

    void OnMouseDown(IMouse _, MouseButton button) => state.PushMouseDown(button);

    void OnMouseUp(IMouse _, MouseButton button) => state.PushMouseUp(button);

    void OnMouseMove(IMouse _, Vector2 position) => state.PushMouseMove(position);

    void OnScroll(IMouse _, ScrollWheel wheel) => state.PushMouseScroll(wheel.Y);
}
