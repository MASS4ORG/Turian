namespace Turian.Engine.Core;

/// <summary>
/// Holds the action maps in force and advances them once per frame from an
/// <see cref="IInputSource"/>. Gameplay code reaches it through the static
/// <see cref="InputActions"/> facade rather than resolving it.
/// </summary>
/// <param name="source">The devices the actions are read from.</param>
public sealed class InputActionService(IInputSource source)
{
    readonly IInputSource source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>The asset currently driving the actions, or null before one is loaded.</summary>
    public InputActionsAsset? Asset { get; private set; }

    /// <summary>Raised whenever a binding is rewritten, so a prompt showing it can be redrawn.</summary>
    public event Action? BindingsChanged;

    /// <summary>Puts an asset's maps in force, replacing whatever was there.</summary>
    /// <param name="asset">The maps to use, or null to unbind everything.</param>
    public void Load(InputActionsAsset? asset)
    {
        foreach (var action in Asset?.Actions ?? []) action.Reset();

        Asset = asset;
        BindingsChanged?.Invoke();
    }

    /// <summary>Finds an action by <c>Map/Action</c> or by a bare name.</summary>
    /// <param name="path">The action to find.</param>
    /// <returns>The action, or null when nothing matches.</returns>
    public InputAction? Find(string path) => Asset?.Find(path);

    /// <summary>Enables or disables a map, so a menu can take input away from gameplay.</summary>
    /// <param name="name">The map's name.</param>
    /// <param name="enabled">Whether it should follow the devices.</param>
    public void SetMapEnabled(string name, bool enabled)
    {
        if (Asset?.Maps.Find(actionMap => string.Equals(actionMap.Name, name, StringComparison.OrdinalIgnoreCase))
            is not { } map) return;

        map.Enabled = enabled;
        if (!enabled)
            foreach (var action in map.Actions) action.Reset();
    }

    /// <summary>
    /// Recomputes every enabled map's actions. Called by <see cref="SceneTicker"/> before the update
    /// passes, so a component reading an action in <c>OnUpdate</c> sees this frame's value.
    /// </summary>
    public void Update()
    {
        if (Asset is null) return;
        if (Rebind is { IsComplete: false }) Rebind.Update(source);

        foreach (var map in Asset.Maps)
        {
            if (!map.Enabled) continue;

            foreach (var action in map.Actions) action.Update(source);
        }
    }

    /// <summary>The rebinding currently listening for a control, or null when none is.</summary>
    public InputRebind? Rebind { get; private set; }

    /// <summary>
    /// Starts listening for the next control the player presses and writes it into one of an action's
    /// bindings. The action itself keeps updating, so a game showing "press any key" should disable
    /// the map first.
    /// </summary>
    /// <param name="action">The action to rebind.</param>
    /// <param name="bindingIndex">Which of its bindings to rewrite.</param>
    /// <returns>The operation, which reports when it has captured something.</returns>
    public InputRebind StartRebind(InputAction action, int bindingIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(action);

        Rebind = new InputRebind(action, bindingIndex, () => BindingsChanged?.Invoke());
        return Rebind;
    }

    /// <summary>Abandons a rebinding in progress, leaving the binding as it was.</summary>
    public void CancelRebind() => Rebind = null;

    /// <summary>
    /// The player's own bindings, as paths keyed by <c>Map/Action/index</c>. What a settings screen
    /// writes to disk and restores on the next run.
    /// </summary>
    /// <returns>Only the bindings that differ from nothing; an empty map when no asset is loaded.</returns>
    public Dictionary<string, string> CaptureOverrides()
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Asset is null) return overrides;

        foreach (var map in Asset.Maps)
            foreach (var action in map.Actions)
                for (var i = 0; i < action.Bindings.Count; i++)
                    overrides[$"{map.Name}/{action.Name}/{i}"] = action.Bindings[i].Path;

        return overrides;
    }

    /// <summary>Copies stored bindings back onto the loaded asset. Entries naming nothing are ignored.</summary>
    /// <param name="overrides">Paths keyed by <c>Map/Action/index</c>.</param>
    public void ApplyOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        if (Asset is null || overrides is null) return;

        foreach (var (key, path) in overrides)
        {
            if (key.Split('/') is not [var mapName, var name, var index]) continue;
            if (!int.TryParse(index, out var slot)) continue;
            if (Asset.Find($"{mapName}/{name}") is not { } action) continue;
            if (slot < 0 || slot >= action.Bindings.Count) continue;

            action.Bindings[slot].Path = path;
        }

        BindingsChanged?.Invoke();
    }
}

/// <summary>
/// A rebinding in progress: it watches for the next control to go down and writes it into the
/// binding it was started for. Modifier keys and Escape are ignored so the player can still cancel.
/// </summary>
public sealed class InputRebind
{
    readonly InputAction action;
    readonly int bindingIndex;
    readonly Action changed;

    internal InputRebind(InputAction action, int bindingIndex, Action changed)
    {
        this.action = action;
        this.bindingIndex = bindingIndex;
        this.changed = changed;
    }

    /// <summary>Whether a control has been captured and written.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>What was captured, or <see cref="InputControl.None"/> while still listening.</summary>
    public InputControl Captured { get; private set; } = InputControl.None;

    /// <summary>Whether the player cancelled instead of pressing something bindable.</summary>
    public bool WasCancelled { get; private set; }

    /// <summary>
    /// Looks for a control that went down this frame. Called by <see cref="InputActionService.Update"/>.
    /// </summary>
    /// <param name="source">The devices to watch.</param>
    /// <returns>True once the operation has finished, whether captured or cancelled.</returns>
    public bool Update(IInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsComplete) return true;

        if (source.WasKeyPressed(Key.Escape))
        {
            WasCancelled = true;
            IsComplete = true;
            return true;
        }

        foreach (var control in InputControl.All())
        {
            if (control.Kind == InputControlKind.Key && IsModifier((Key)control.Code)) continue;
            if (!source.WentDown(control)) continue;

            Captured = control;
            IsComplete = true;

            if (bindingIndex >= 0 && bindingIndex < action.Bindings.Count)
                action.Bindings[bindingIndex].Path = control.ToString();

            changed();
            return true;
        }

        return false;
    }

    /// <summary>A modifier alone is never what a player meant to bind, so it does not end the listen.</summary>
    static bool IsModifier(Key key) => key
        is Key.ControlLeft or Key.ControlRight or Key.ShiftLeft or Key.ShiftRight
        or Key.AltLeft or Key.AltRight or Key.SuperLeft or Key.SuperRight;
}

/// <summary>
/// Static entry point for reading input actions from gameplay code, the action-level counterpart of
/// <see cref="Input"/>.
///
/// <example>
/// <code>
/// public override void OnUpdate(float deltaTime)
/// {
///     var move = InputActions.ReadVector("Move");
///     if (InputActions.WasPressed("Jump")) Jump();
/// }
/// </code>
/// </example>
/// </summary>
/// <remarks>
/// Resolves an <see cref="InputActionService"/> from <see cref="RuntimeServices"/> on each call, so a
/// component works unchanged in the standalone runtime and in the Studio's play mode. With no service
/// registered — or no asset loaded — every query returns a neutral value instead of throwing.
/// </remarks>
public static class InputActions
{
    /// <summary>The active action service, or null when actions are not routed.</summary>
    public static InputActionService? Service => RuntimeServices.TryGet<InputActionService>();

    /// <summary>Finds an action by <c>Map/Action</c> or by a bare name.</summary>
    /// <param name="path">The action to find.</param>
    /// <returns>The action, or null when nothing matches.</returns>
    public static InputAction? Find(string path) => Service?.Find(path);

    /// <summary>Whether an action is currently held.</summary>
    /// <param name="path">The action to read.</param>
    public static bool IsPressed(string path) => Find(path)?.IsPressed ?? false;

    /// <summary>Whether an action went down during this frame.</summary>
    /// <param name="path">The action to read.</param>
    public static bool WasPressed(string path) => Find(path)?.WasPressed ?? false;

    /// <summary>Whether an action went up during this frame.</summary>
    /// <param name="path">The action to read.</param>
    public static bool WasReleased(string path) => Find(path)?.WasReleased ?? false;

    /// <summary>An action's value as a single axis, −1 to 1.</summary>
    /// <param name="path">The action to read.</param>
    public static float ReadAxis(string path) => Find(path)?.ReadAxis() ?? 0f;

    /// <summary>An action's value as a direction.</summary>
    /// <param name="path">The action to read.</param>
    public static Vector2 ReadVector(string path) => Find(path)?.ReadVector() ?? Vector2.Zero;

    /// <summary>Enables or disables a whole map, so a menu can take input away from gameplay.</summary>
    /// <param name="name">The map's name.</param>
    /// <param name="enabled">Whether it should follow the devices.</param>
    public static void SetMapEnabled(string name, bool enabled) => Service?.SetMapEnabled(name, enabled);
}
