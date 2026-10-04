namespace Turian.Tests;

/// <summary>Checks configurable held navigation and its isolation from text entry and tool shortcuts.</summary>
public sealed class SceneNavigationTests
{
    /// <summary>Only arrows navigate by default, and focus, typing and capture suspend movement.</summary>
    [Fact]
    public void NavigationUsesArrowsAndEffectiveBindings()
    {
        var shortcuts = new ShortcutService(NullLogger.Instance);
        var context = Substitute.For<IPluginContext>();
        context.Shortcuts.Returns(shortcuts);
        SceneNavigationBindings.Register(context);
        var focus = Substitute.For<IFocusTracker>();
        focus.ActivePanelId.Returns(GayaPlugin.ViewportPanelId);
        var navigation = new SceneNavigationBindings(shortcuts, focus);
        var input = Substitute.For<IInputHandler>();
        var keys = new HashSet<KeyboardKey>();
        input.IsKeyDown(Arg.Any<KeyboardKey>()).Returns(call => keys.Contains(call.Arg<KeyboardKey>()));
        var held = new HashSet<int>();
        keys.UnionWith([KeyboardKey.W, KeyboardKey.A, KeyboardKey.S, KeyboardKey.D, KeyboardKey.Q, KeyboardKey.E]);
        navigation.Read(input, held, false);
        Assert.Empty(held);
        keys.Clear();
        keys.UnionWith([KeyboardKey.Up, KeyboardKey.Down, KeyboardKey.Left, KeyboardKey.Right]);
        navigation.Read(input, held, false);
        Assert.Equal([0, 1, 2, 3], held.Order());
        Assert.Null(shortcuts.Entries.Single(entry => entry.CommandId == SceneNavigationBindings.CommandId(4)).Effective);
        Assert.False(shortcuts.Entries.Single(entry => entry.CommandId == SceneNavigationBindings.CommandId(4)).IsModified);
        Assert.Equal("Up", navigation.DisplayFor(SceneNavigationBindings.CommandId(0)));
        Assert.Empty(shortcuts.Conflicts);
        keys.Clear();
        keys.UnionWith([KeyboardKey.Up, KeyboardKey.LeftShift]);
        navigation.Read(input, held, false);
        Assert.Equal([0], held);
        navigation.Read(input, held, true);
        Assert.Empty(held);
        focus.ActivePanelId.Returns(GayaPlugin.AssetsPanelId);
        navigation.Read(input, held, false);
        Assert.Empty(held);
        focus.ActivePanelId.Returns(GayaPlugin.ViewportPanelId);
        shortcuts.IsCapturing = true;
        navigation.Read(input, held, false);
        Assert.Empty(held);
        shortcuts.IsCapturing = false;
        shortcuts.Rebind(SceneNavigationBindings.CommandId(0), new KeyStroke(KeyboardKey.I));
        navigation.Read(input, held, false);
        Assert.Empty(held);
        keys.Add(KeyboardKey.I);
        navigation.Read(input, held, false);
        Assert.Equal([0], held);
        shortcuts.Rebind(SceneNavigationBindings.CommandId(0), KeyStroke.None);
        navigation.Read(input, held, false);
        Assert.Empty(held);
        shortcuts.Rebind(SceneNavigationBindings.CommandId(4), new KeyStroke(KeyboardKey.PageUp));
        keys.Clear();
        keys.Add(KeyboardKey.PageUp);
        navigation.Read(input, held, false);
        Assert.Equal([4], held);
        shortcuts.ResetAll();
        navigation.Read(input, held, false);
        Assert.Empty(held);
    }

    /// <summary>Held bindings honor modifiers, shadowing, chords and overrides without also dispatching a press.</summary>
    [Fact]
    public void ContinuousShortcutsHonorShortcutRules()
    {
        var shortcuts = new ShortcutService(NullLogger.Instance);
        var binding = new KeyBinding("move", KeyboardKey.Up, context: GayaPlugin.ViewportPanelId) { IsContinuous = true };
        shortcuts.Add(binding);
        var input = Substitute.For<IInputHandler>();
        var keys = new HashSet<KeyboardKey>();
        input.IsKeyDown(Arg.Any<KeyboardKey>()).Returns(call => keys.Contains(call.Arg<KeyboardKey>()));
        input.IsKeyPressed(Arg.Any<KeyboardKey>()).Returns(call => keys.Contains(call.Arg<KeyboardKey>()));
        Assert.False(Held());
        keys.Add(KeyboardKey.Up);
        Assert.True(Held());
        Assert.Null(shortcuts.Dispatch(input, GayaPlugin.ViewportPanelId));
        Assert.False(shortcuts.IsHeld("missing", input, GayaPlugin.ViewportPanelId));
        Assert.False(shortcuts.IsHeld("move", input, GayaPlugin.AssetsPanelId));
        foreach (var key in new[] { KeyboardKey.LeftControl, KeyboardKey.LeftAlt, KeyboardKey.LeftShift })
        {
            keys.Add(key);
            Assert.False(Held());
            keys.Remove(key);
        }
        keys.Add(KeyboardKey.RightShift);
        Assert.True(shortcuts.IsHeld("move", input, GayaPlugin.ViewportPanelId, KeyModifiers.Shift));
        keys.Clear();
        keys.UnionWith([KeyboardKey.RightControl, KeyboardKey.RightAlt, KeyboardKey.RightShift, KeyboardKey.Up]);
        shortcuts.Rebind("move", new KeyStroke(KeyboardKey.Up, KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift));
        Assert.True(Held());
        keys.Remove(KeyboardKey.RightControl);
        Assert.False(Held());
        Assert.Throws<ArgumentException>(() => shortcuts.Rebind("move", new KeyStroke(KeyboardKey.K),
            new KeyStroke(KeyboardKey.S)));
        shortcuts.ResetAll();
        keys.Clear();
        keys.Add(KeyboardKey.Up);
        shortcuts.Add(new KeyBinding("global", KeyboardKey.Up));
        Assert.Equal("move", shortcuts.Entries.First(entry => entry.CommandId == "move").CommandId);
        Assert.True(Held());
        shortcuts.Remove("move");
        Assert.False(Held());
        shortcuts.Add(binding with { Context = ShortcutContexts.Global });
        shortcuts.Add(new KeyBinding("scoped", KeyboardKey.Up, context: GayaPlugin.ViewportPanelId));
        Assert.False(Held());
        shortcuts.Remove("scoped");
        shortcuts.Remove("global");
        shortcuts.Add(new KeyBinding("chord", new KeyStroke(KeyboardKey.K), new KeyStroke(KeyboardKey.S)));
        keys.Clear();
        keys.Add(KeyboardKey.K);
        shortcuts.Dispatch(input, GayaPlugin.ViewportPanelId);
        keys.Add(KeyboardKey.Up);
        Assert.False(Held());
        shortcuts.CancelPending();
        Assert.True(Held());
        shortcuts.Remove("move");
        shortcuts.Add(binding with { Second = new KeyStroke(KeyboardKey.S) });
        Assert.False(Held());
        shortcuts.Add(binding with { Stroke = KeyStroke.None });
        Assert.False(Held());

        bool Held() => shortcuts.IsHeld("move", input, GayaPlugin.ViewportPanelId);
    }
}
