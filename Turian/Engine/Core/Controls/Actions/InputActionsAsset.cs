namespace Turian.Engine.Core;

/// <summary>
/// The project's action maps as an authored asset: what the game's actions are and which controls
/// drive them. Created from the asset browser like any other data asset, edited in the inspector,
/// and handed to <see cref="InputActionService"/> at startup.
/// </summary>
[CreateAssetMenu(fileName: "InputActions", path: "Input/Input Actions")]
[TypeId("a3000003-0000-4000-8000-000000000001")]
public class InputActionsAsset : DataAsset
{
    /// <summary>The maps this asset defines.</summary>
    public List<InputActionMap> Maps { get; set; } = [];

    /// <summary>
    /// Finds an action by <c>Map/Action</c>, or by a bare action name searched across every map.
    /// </summary>
    /// <param name="path">The action to find, e.g. <c>Gameplay/Jump</c> or just <c>Jump</c>.</param>
    /// <returns>The action, or null when nothing matches.</returns>
    public InputAction? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (path.Split('/', StringSplitOptions.TrimEntries) is [var map, var action])
            return Maps.Find(m => string.Equals(m.Name, map, StringComparison.OrdinalIgnoreCase))?.Find(action);

        return Maps.Select(m => m.Find(path)).FirstOrDefault(found => found is not null);
    }

    /// <summary>Every action in every map, in declaration order.</summary>
    public IEnumerable<InputAction> Actions => Maps.SelectMany(map => map.Actions);

    /// <summary>
    /// The default maps a project gets before anything is authored: WASD and a left stick driving
    /// <c>Move</c>, the mouse and right stick driving <c>Look</c>, and Space or the south face
    /// button driving <c>Jump</c>.
    /// </summary>
    /// <returns>A freshly built asset.</returns>
    public static InputActionsAsset CreateDefault()
    {
        var move = new InputAction { Name = "Move", Type = InputActionType.Vector2 };
        Compose(move, InputControl.Keyboard(Key.W), InputControl.Keyboard(Key.S),
            InputControl.Keyboard(Key.A), InputControl.Keyboard(Key.D));
        move.Bindings.Add(InputBinding.For(InputControl.GamepadStick(GamepadAxis.LeftStickX), Vector2.UnitX));
        move.Bindings.Add(InputBinding.For(InputControl.GamepadStick(GamepadAxis.LeftStickY), Vector2.UnitY));

        var look = new InputAction { Name = "Look", Type = InputActionType.Vector2 };
        look.Bindings.Add(InputBinding.For(InputControl.MouseMotion(MouseAxis.DeltaX), Vector2.UnitX));
        look.Bindings.Add(InputBinding.For(InputControl.MouseMotion(MouseAxis.DeltaY), -Vector2.UnitY));
        look.Bindings.Add(InputBinding.For(InputControl.GamepadStick(GamepadAxis.RightStickX), Vector2.UnitX));
        look.Bindings.Add(InputBinding.For(InputControl.GamepadStick(GamepadAxis.RightStickY), Vector2.UnitY));

        var jump = new InputAction { Name = "Jump" };
        jump.Bindings.Add(InputBinding.For(InputControl.Keyboard(Key.Space)));
        jump.Bindings.Add(InputBinding.For(InputControl.Gamepad(GamepadButton.South)));

        return new InputActionsAsset
        {
            Maps = [new InputActionMap { Name = "Gameplay", Actions = [move, look, jump] }],
        };
    }

    /// <summary>Wires four buttons as the up, down, left and right of one vector action.</summary>
    static void Compose(InputAction action, InputControl up, InputControl down,
        InputControl left, InputControl right)
    {
        action.Bindings.Add(InputBinding.For(up, Vector2.UnitY));
        action.Bindings.Add(InputBinding.For(down, -Vector2.UnitY));
        action.Bindings.Add(InputBinding.For(left, -Vector2.UnitX));
        action.Bindings.Add(InputBinding.For(right, Vector2.UnitX));
    }
}
