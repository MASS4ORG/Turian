namespace Turian.Engine.Core;

/// <summary>
/// What shape of value an <see cref="InputAction"/> produces. Serialized by name, because an input
/// asset is routinely hand-edited and a bare number says nothing about what it means.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InputActionType>))]
public enum InputActionType
{
    /// <summary>On or off. <see cref="InputAction.IsPressed"/> and its edges are the useful reads.</summary>
    Button,

    /// <summary>A single value from −1 to 1. Read through <see cref="InputAction.ReadAxis"/>.</summary>
    Axis,

    /// <summary>A direction. Read through <see cref="InputAction.ReadVector"/>.</summary>
    Vector2,
}

/// <summary>
/// One physical control wired to an action, together with what it contributes. A button binding's
/// <see cref="Value"/> is the direction it pushes the action in — W contributes <c>(0, 1)</c> and S
/// <c>(0, −1)</c> — and an axis binding's scales the reading, so <c>(−1, 0)</c> inverts it.
/// </summary>
public sealed class InputBinding
{
    string parsedPath = string.Empty;
    InputControl parsed;

    /// <summary>The control this binding reads, as a path such as <c>Keyboard/W</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>What the control contributes when held, or what an axis reading is multiplied by.</summary>
    public Vector2 Value { get; set; } = Vector2.UnitX;

    /// <summary>The player slot a gamepad control must come from, or −1 for any attached gamepad.</summary>
    public int PlayerIndex { get; set; } = -1;

    /// <summary>The control <see cref="Path"/> names, reparsed whenever the path is rewritten.</summary>
    [JsonIgnore]
    [HideInEditor]
    public InputControl Control
    {
        get
        {
            if (!string.Equals(parsedPath, Path, StringComparison.Ordinal))
            {
                parsed = InputControl.Parse(Path);
                parsedPath = Path;
            }

            return parsed;
        }
    }

    /// <summary>Builds a binding for one control.</summary>
    /// <param name="control">The control to read.</param>
    /// <param name="value">What it contributes; defaults to the positive X direction.</param>
    /// <returns>The binding.</returns>
    public static InputBinding For(InputControl control, Vector2? value = null) =>
        new() { Path = control.ToString(), Value = value ?? Vector2.UnitX };
}

/// <summary>
/// A named thing the game reacts to — <c>Jump</c>, <c>Move</c>, <c>Look</c> — with the controls that
/// drive it. Gameplay code polls this or subscribes to it and never names a key, which is what makes
/// a binding rebindable at runtime.
/// </summary>
/// <remarks>
/// A frame's value is the sum of every binding's contribution, clamped to the unit square for a
/// vector or to −1..1 for an axis, so <c>A</c> and <c>D</c> held together cancel out the way a stick
/// at rest does.
/// </remarks>
public sealed class InputAction
{
    Vector2 value;
    bool pressed;
    bool wasPressed;
    bool wasReleased;

    /// <summary>The action's name, unique inside its map.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What shape of value the action produces.</summary>
    public InputActionType Type { get; set; } = InputActionType.Button;

    /// <summary>The controls that drive it. Order is the order a rebinding addresses them by.</summary>
    public List<InputBinding> Bindings { get; set; } = [];

    /// <summary>Whether the action is currently held.</summary>
    [JsonIgnore]
    [HideInEditor]
    public bool IsPressed => pressed;

    /// <summary>Whether the action went down during this frame.</summary>
    [JsonIgnore]
    [HideInEditor]
    public bool WasPressed => wasPressed;

    /// <summary>Whether the action went up during this frame.</summary>
    [JsonIgnore]
    [HideInEditor]
    public bool WasReleased => wasReleased;

    /// <summary>Raised on the frame the action went down.</summary>
    public event Action<InputAction>? Started;

    /// <summary>Raised on any frame the action's value changed while it was not at rest.</summary>
    public event Action<InputAction>? Performed;

    /// <summary>Raised on the frame the action went up.</summary>
    public event Action<InputAction>? Canceled;

    /// <summary>The action's value as a direction. Meaningful for every type; X alone for an axis.</summary>
    /// <returns>The current vector.</returns>
    public Vector2 ReadVector() => value;

    /// <summary>The action's value as a single axis.</summary>
    /// <returns>The current value, −1 to 1.</returns>
    public float ReadAxis() => value.X;

    /// <summary>
    /// Recomputes the action from its bindings and raises whatever the change implies. Called once a
    /// frame by <see cref="InputActionService"/>, before the scene's update passes run.
    /// </summary>
    /// <param name="source">The devices to read.</param>
    public void Update(IInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var previous = value;
        var wasDown = pressed;

        value = Accumulate(source);
        pressed = Type == InputActionType.Button
            ? value.X >= InputSourceExtensions.PressThreshold
            : value.Length() >= InputSourceExtensions.PressThreshold;

        wasPressed = pressed && !wasDown;
        wasReleased = !pressed && wasDown;

        if (wasPressed) Started?.Invoke(this);
        if (value != previous && value != Vector2.Zero) Performed?.Invoke(this);
        if (wasReleased) Canceled?.Invoke(this);
    }

    /// <summary>Drops the action to rest without raising anything, for a map that was just disabled.</summary>
    public void Reset()
    {
        value = Vector2.Zero;
        pressed = false;
        wasPressed = false;
        wasReleased = false;
    }

    Vector2 Accumulate(IInputSource source)
    {
        var total = Vector2.Zero;

        foreach (var binding in Bindings)
        {
            var control = binding.Control;
            if (control.Kind == InputControlKind.None) continue;

            var reading = source.Read(control, binding.PlayerIndex);
            if (reading == 0f) continue;

            // A button contributes its whole direction; an axis scales it, so one stick axis bound to
            // (0, 1) drives the same action a W key bound to (0, 1) does.
            total += control.IsAxis
                ? binding.Value * reading
                : binding.Value * (control.Kind == InputControlKind.Key || reading >= InputSourceExtensions.PressThreshold
                    ? 1f
                    : 0f);
        }

        return Type == InputActionType.Vector2
            ? Clamp(total)
            : new Vector2(Math.Clamp(total.X, -1f, 1f), 0f);
    }

    /// <summary>Clamps each component rather than the length, so a diagonal keeps both directions.</summary>
    static Vector2 Clamp(Vector2 raw) =>
        new(Math.Clamp(raw.X, -1f, 1f), Math.Clamp(raw.Y, -1f, 1f));
}

/// <summary>
/// A set of actions that belong together and are enabled or disabled as one — gameplay, menu,
/// vehicle. Only an enabled map's actions follow the devices.
/// </summary>
public sealed class InputActionMap
{
    /// <summary>The map's name, unique inside its asset.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the map follows the devices. A disabled map's actions sit at rest.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The actions this map holds.</summary>
    public List<InputAction> Actions { get; set; } = [];

    /// <summary>Finds an action by name, or null when the map has none by that name.</summary>
    /// <param name="name">The action's name, compared case-insensitively.</param>
    public InputAction? Find(string name) =>
        Actions.Find(action => string.Equals(action.Name, name, StringComparison.OrdinalIgnoreCase));
}
