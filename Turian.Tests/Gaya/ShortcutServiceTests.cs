namespace Turian.Tests;

/// <summary>
/// Covers the shortcut layer: how a key sequence reads and prints, how a user override replaces a
/// contributed binding, how a focused panel selects between two bindings on the same keys, and what
/// counts as a conflict.
/// </summary>
public class ShortcutServiceTests
{
    const string panel = "gaya.test.panel";

    /// <summary>Verifies that a printed keystroke parses back to the same key and modifiers.</summary>
    [Theory]
    [InlineData("Ctrl+S", KeyboardKey.S, KeyModifiers.Ctrl)]
    [InlineData("ctrl+shift+s", KeyboardKey.S, KeyModifiers.Ctrl | KeyModifiers.Shift)]
    [InlineData("F2", KeyboardKey.F2, KeyModifiers.None)]
    [InlineData("Ctrl+,", KeyboardKey.Comma, KeyModifiers.Ctrl)]
    [InlineData("Alt+1", KeyboardKey.D1, KeyModifiers.Alt)]
    public void StrokesParseFromTheFormTheyPrint(string text, KeyboardKey key, KeyModifiers modifiers)
    {
        Assert.True(KeyStroke.TryParse(text, out var stroke));
        Assert.Equal(new KeyStroke(key, modifiers), stroke);
        Assert.True(KeyStroke.TryParse(stroke.ToString(), out var round));
        Assert.Equal(stroke, round);
    }

    /// <summary>Rejects malformed keystroke descriptions.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+S")]
    [InlineData("NotAKey")]
    public void NonsenseIsNotAStroke(string text) => Assert.False(KeyStroke.TryParse(text, out _));

    /// <summary>A chord prints its two strokes comma-separated, and reads back the same way.</summary>
    [Fact]
    public void ChordsRoundTripThroughTheirDisplayForm()
    {
        var binding = new KeyBinding("cmd", new KeyStroke(KeyboardKey.K, KeyModifiers.Ctrl),
            new KeyStroke(KeyboardKey.S, KeyModifiers.Ctrl));

        Assert.Equal("Ctrl+K, Ctrl+S", binding.Display);
        Assert.True(KeyBinding.TryParseSequence(binding.Display, out var strokes));
        Assert.Equal(binding.Stroke, strokes.First);
        Assert.Equal(binding.Second, strokes.Second);
    }

    /// <summary>Dispatches a matching single-stroke binding.</summary>
    [Fact]
    public void ASingleStrokeRunsItsCommand()
    {
        var shortcuts = Service(new KeyBinding("save", KeyboardKey.S, KeyModifiers.Ctrl));

        Assert.Equal("save", shortcuts.Dispatch(Keys(KeyboardKey.S, KeyModifiers.Ctrl), ShortcutContexts.Global));
    }

    /// <summary>Modifiers are matched exactly, so Ctrl+Shift+S never also fires Ctrl+S.</summary>
    [Fact]
    public void AnExtraModifierIsADifferentSequence()
    {
        var shortcuts = Service(new KeyBinding("save", KeyboardKey.S, KeyModifiers.Ctrl));

        Assert.Null(shortcuts.Dispatch(Keys(KeyboardKey.S, KeyModifiers.Ctrl | KeyModifiers.Shift),
            ShortcutContexts.Global));
    }

    /// <summary>The first stroke of a chord arms it and runs nothing; the second completes it.</summary>
    [Fact]
    public void AChordNeedsBothStrokes()
    {
        var shortcuts = Service(new KeyBinding("shortcuts",
            new KeyStroke(KeyboardKey.K, KeyModifiers.Ctrl), new KeyStroke(KeyboardKey.S, KeyModifiers.Ctrl)));

        Assert.Null(shortcuts.Dispatch(Keys(KeyboardKey.K, KeyModifiers.Ctrl), ShortcutContexts.Global));
        Assert.Equal("shortcuts",
            shortcuts.Dispatch(Keys(KeyboardKey.S, KeyModifiers.Ctrl), ShortcutContexts.Global));
    }

    /// <summary>A stroke that completes nothing cancels the armed chord rather than being swallowed.</summary>
    [Fact]
    public void AnUnmatchedSecondStrokeCancelsTheChord()
    {
        var shortcuts = Service(
            new KeyBinding("shortcuts", new KeyStroke(KeyboardKey.K, KeyModifiers.Ctrl),
                new KeyStroke(KeyboardKey.T, KeyModifiers.Ctrl)),
            new KeyBinding("save", KeyboardKey.S, KeyModifiers.Ctrl));

        shortcuts.Dispatch(Keys(KeyboardKey.K, KeyModifiers.Ctrl), ShortcutContexts.Global);
        Assert.Equal("save", shortcuts.Dispatch(Keys(KeyboardKey.S, KeyModifiers.Ctrl), ShortcutContexts.Global));
        Assert.True(shortcuts.Pending.IsNone);
    }

    /// <summary>A panel-scoped binding only fires while that panel is the focused one.</summary>
    [Fact]
    public void APanelBindingNeedsItsPanelFocused()
    {
        var shortcuts = Service(new KeyBinding("delete", KeyboardKey.Delete, KeyModifiers.None, panel));

        Assert.Null(shortcuts.Dispatch(Keys(KeyboardKey.Delete), ShortcutContexts.Global));
        Assert.Equal("delete", shortcuts.Dispatch(Keys(KeyboardKey.Delete), panel));
    }

    /// <summary>With both bound to the same keys, the focused panel's binding wins over the global one.</summary>
    [Fact]
    public void ThePanelBindingShadowsTheGlobalOne()
    {
        var shortcuts = Service(
            new KeyBinding("global", KeyboardKey.D, KeyModifiers.Ctrl),
            new KeyBinding("scoped", KeyboardKey.D, KeyModifiers.Ctrl, panel));

        Assert.Equal("global", shortcuts.Dispatch(Keys(KeyboardKey.D, KeyModifiers.Ctrl), ShortcutContexts.Global));
        Assert.Equal("scoped", shortcuts.Dispatch(Keys(KeyboardKey.D, KeyModifiers.Ctrl), panel));
    }

    /// <summary>Confirms that a user rebind replaces the contributed shortcut.</summary>
    [Fact]
    public void AnOverrideReplacesTheContributedBinding()
    {
        var shortcuts = Service(new KeyBinding("save", KeyboardKey.S, KeyModifiers.Ctrl));
        shortcuts.Rebind("save", new KeyStroke(KeyboardKey.F4, KeyModifiers.Alt));

        Assert.Null(shortcuts.Dispatch(Keys(KeyboardKey.S, KeyModifiers.Ctrl), ShortcutContexts.Global));
        Assert.Equal("save", shortcuts.Dispatch(Keys(KeyboardKey.F4, KeyModifiers.Alt), ShortcutContexts.Global));
        Assert.Equal("Alt+F4", shortcuts.DisplayFor("save"));
        Assert.True(shortcuts.Entries.Single().IsModified);
    }

    /// <summary>Rebinding to the empty stroke unbinds a command without giving it other keys.</summary>
    [Fact]
    public void ACommandCanBeUnbound()
    {
        var shortcuts = Service(new KeyBinding("save", KeyboardKey.S, KeyModifiers.Ctrl));
        shortcuts.Rebind("save", KeyStroke.None);

        Assert.Null(shortcuts.Dispatch(Keys(KeyboardKey.S, KeyModifiers.Ctrl), ShortcutContexts.Global));
        Assert.Equal(string.Empty, shortcuts.DisplayFor("save"));
    }

    /// <summary>Confirms that resetting a binding restores its contributed default.</summary>
    [Fact]
    public void ResetRestoresWhatWasContributed()
    {
        var shortcuts = Service(new KeyBinding("save", KeyboardKey.S, KeyModifiers.Ctrl));
        shortcuts.Rebind("save", new KeyStroke(KeyboardKey.F4, KeyModifiers.Alt));
        shortcuts.Reset("save");

        Assert.Equal("Ctrl+S", shortcuts.DisplayFor("save"));
        Assert.False(shortcuts.Entries.Single().IsModified);
    }

    /// <summary>Two commands on the same keys in the same context clash; in different panels they do not.</summary>
    [Fact]
    public void OnlyOverlappingContextsConflict()
    {
        var separate = Service(
            new KeyBinding("tree", KeyboardKey.Delete, KeyModifiers.None, "panel.a"),
            new KeyBinding("assets", KeyboardKey.Delete, KeyModifiers.None, "panel.b"));

        Assert.Empty(separate.Conflicts);

        var clashing = Service(
            new KeyBinding("save", KeyboardKey.S, KeyModifiers.Ctrl),
            new KeyBinding("other", KeyboardKey.S, KeyModifiers.Ctrl));

        var conflict = Assert.Single(clashing.Conflicts);
        Assert.Equal("Ctrl+S", conflict.Display);
        Assert.Equal(["other", "save"], conflict.CommandIds.Order());
    }

    /// <summary>A global binding does clash with a panel's, because it also fires inside that panel.</summary>
    [Fact]
    public void AGlobalBindingClashesWithAPanelScopedOne()
    {
        var shortcuts = Service(
            new KeyBinding("global", KeyboardKey.D, KeyModifiers.Ctrl),
            new KeyBinding("scoped", KeyboardKey.D, KeyModifiers.Ctrl, panel));

        Assert.Single(shortcuts.Conflicts);
        Assert.Equal(["global"], shortcuts.ConflictsFor("scoped", new KeyStroke(KeyboardKey.D, KeyModifiers.Ctrl)));
        Assert.Empty(shortcuts.ConflictsFor("scoped", new KeyStroke(KeyboardKey.G, KeyModifiers.Ctrl)));
    }

    /// <summary>A stored sequence that no longer reads as one leaves the contributed binding in force.</summary>
    [Fact]
    public void AnUnreadableOverrideFallsBackToTheDefault()
    {
        var shortcuts = Service(new KeyBinding("save", KeyboardKey.S, KeyModifiers.Ctrl));
        shortcuts.Rebind("save", new KeyStroke(KeyboardKey.F4));

        var page = Assert.IsType<ShortcutOverrides>(StoredPage(shortcuts));
        page.Bindings["save"] = "Ctrl+Nonsense";

        Assert.Equal("Ctrl+S", shortcuts.DisplayFor("save"));
    }

    static ShortcutService Service(params KeyBinding[] bindings)
    {
        var settings = Substitute.For<IEditorSettings>();
        var service = new ShortcutService(NullLogger.Instance, settings);

        foreach (var binding in bindings) service.Add(binding);
        return service;
    }

    /// <summary>The overrides object the service registered as its settings page and writes through.</summary>
    static object StoredPage(ShortcutService service) =>
        service.GetType()
            .GetField("overrides", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;

    /// <summary>An input handler reporting exactly one key press with the given modifiers held.</summary>
    static IInputHandler Keys(KeyboardKey key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var input = Substitute.For<IInputHandler>();
        input.IsKeyPressed(Arg.Any<KeyboardKey>()).Returns(call => call.Arg<KeyboardKey>() == key);
        input.IsKeyDown(Arg.Any<KeyboardKey>()).Returns(call => call.Arg<KeyboardKey>() switch
        {
            KeyboardKey.LeftControl or KeyboardKey.RightControl => modifiers.HasFlag(KeyModifiers.Ctrl),
            KeyboardKey.LeftShift or KeyboardKey.RightShift => modifiers.HasFlag(KeyModifiers.Shift),
            KeyboardKey.LeftAlt or KeyboardKey.RightAlt => modifiers.HasFlag(KeyModifiers.Alt),
            _ => false,
        });

        return input;
    }
}
