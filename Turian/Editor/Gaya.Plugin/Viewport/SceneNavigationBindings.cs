namespace Gaya.Plugin.Turian;

/// <summary>Connects continuous, focus-scoped shortcut bindings to camera movement directions.</summary>
sealed class SceneNavigationBindings(IShortcutService shortcuts, IFocusTracker focus)
{
    internal static readonly (string Id, string Label, KeyboardKey Key)[] Directions =
    [
        ("forward", "Forward", KeyboardKey.Up),
        ("backward", "Backward", KeyboardKey.Down),
        ("left", "Left", KeyboardKey.Left),
        ("right", "Right", KeyboardKey.Right),
        ("up", "Up", KeyboardKey.Unknown),
        ("down", "Down", KeyboardKey.Unknown)
    ];

    /// <summary>Returns the stable command id of a camera movement direction.</summary>
    public static string CommandId(int direction) => "gaya.turian.viewport.camera." + Directions[direction].Id;

    /// <summary>Returns the user's current shortcut label for a Scene tool or navigation command.</summary>
    public string DisplayFor(string commandId) => shortcuts.DisplayFor(commandId);

    /// <summary>Populates movement directions only while Scene view is focused and input is not being edited.</summary>
    public void Read(IInputHandler input, HashSet<int> held, bool editingText)
    {
        held.Clear();
        if (editingText || focus.ActivePanelId != GayaPlugin.ViewportPanelId) return;
        for (var i = 0; i < Directions.Length; i++)
            if (shortcuts.IsHeld(CommandId(i), input, focus.ActivePanelId, KeyModifiers.Shift)) held.Add(i);
    }

    /// <summary>Registers editable movement bindings and commands that nudge the camera from the palette.</summary>
    public static void Register(IPluginContext context)
    {
        for (var i = 0; i < Directions.Length; i++)
        {
            var direction = i;
            var definition = Directions[i];
            var id = CommandId(i);
            context.Commands.Register(new CommandDescriptor(id, "Scene View: Move Camera " + definition.Label,
                services => (services.GetService<IPanelAccessor>()?.Panel(GayaPlugin.ViewportPanelId) as ScenePanel)
                    ?.MoveCamera(direction)));
            context.Shortcuts.Add(new KeyBinding(id, definition.Key, context: GayaPlugin.ViewportPanelId)
            {
                IsContinuous = true
            });
        }
    }
}
