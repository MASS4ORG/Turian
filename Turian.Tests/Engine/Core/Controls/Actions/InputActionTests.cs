using Silk.NET.Input;

namespace Turian.Tests;

/// <summary>
/// Covers the action layer: how a binding path reads and prints, how several bindings compose into a
/// button, an axis and a vector, what the per-frame edges report, and how a rebinding and its stored
/// overrides rewrite what a control is wired to.
/// </summary>
public class InputActionTests
{
    /// <summary>Verifies that a printed control path parses back to the same control kind.</summary>
    [Theory]
    [InlineData("Keyboard/W", InputControlKind.Key)]
    [InlineData("Mouse/Left", InputControlKind.MouseButton)]
    [InlineData("Mouse/DeltaX", InputControlKind.MouseAxis)]
    [InlineData("Gamepad/South", InputControlKind.GamepadButton)]
    [InlineData("Gamepad/LeftStickX", InputControlKind.GamepadAxis)]
    public void ControlsParseFromThePathTheyPrint(string path, InputControlKind kind)
    {
        Assert.True(InputControl.TryParse(path, out var control));
        Assert.Equal(kind, control.Kind);
        Assert.Equal(path, control.ToString());
    }

    /// <summary>Rejects malformed or unknown input-control paths.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Keyboard")]
    [InlineData("Keyboard/NotAKey")]
    [InlineData("Joystick/W")]
    public void NonsenseNamesNoControl(string path)
    {
        Assert.False(InputControl.TryParse(path, out _));
        Assert.Equal(InputControl.None, InputControl.Parse(path));
    }

    /// <summary>A button action is pressed while any of its controls is held, keyboard or gamepad.</summary>
    [Fact]
    public void AnyBindingCanPressAButtonAction()
    {
        var input = new BufferedInputSource();
        var jump = Button("Jump", InputControl.Keyboard(Key.Space), InputControl.Gamepad(GamepadButton.South));

        jump.Update(input);
        Assert.False(jump.IsPressed);

        input.PushGamepadButtonDown(0, GamepadButton.South);
        jump.Update(input);
        Assert.True(jump.IsPressed);
    }

    /// <summary>The edges fire once each: pressed on the frame it goes down, released when it goes up.</summary>
    [Fact]
    public void EdgesAreReportedForOneFrameOnly()
    {
        var input = new BufferedInputSource();
        var jump = Button("Jump", InputControl.Keyboard(Key.Space));
        var started = 0;
        var canceled = 0;
        jump.Started += _ => started++;
        jump.Canceled += _ => canceled++;

        input.PushKeyDown(Key.Space);
        jump.Update(input);
        Assert.True(jump.WasPressed);
        Assert.False(jump.WasReleased);

        jump.Update(input);
        Assert.False(jump.WasPressed);
        Assert.True(jump.IsPressed);

        input.PushKeyUp(Key.Space);
        jump.Update(input);
        Assert.True(jump.WasReleased);
        Assert.False(jump.IsPressed);

        Assert.Equal(1, started);
        Assert.Equal(1, canceled);
    }

    /// <summary>Two buttons pushing opposite ways cancel out, the way a stick at rest reads zero.</summary>
    [Fact]
    public void OpposedButtonsComposeIntoAnAxis()
    {
        var input = new BufferedInputSource();
        var action = new InputAction { Name = "Turn", Type = InputActionType.Axis };
        action.Bindings.Add(InputBinding.For(InputControl.Keyboard(Key.A), -Vector2.UnitX));
        action.Bindings.Add(InputBinding.For(InputControl.Keyboard(Key.D), Vector2.UnitX));

        input.PushKeyDown(Key.D);
        action.Update(input);
        Assert.Equal(1f, action.ReadAxis());

        input.PushKeyDown(Key.A);
        action.Update(input);
        Assert.Equal(0f, action.ReadAxis());
    }

    /// <summary>WASD composes into a direction, and a diagonal keeps both of its components.</summary>
    [Fact]
    public void FourButtonsComposeIntoAVector()
    {
        var input = new BufferedInputSource();
        var move = Assert.IsType<InputAction>(InputActionsAsset.CreateDefault().Find("Gameplay/Move"));

        input.PushKeyDown(Key.W);
        input.PushKeyDown(Key.D);
        move.Update(input);

        Assert.Equal(new Vector2(1f, 1f), move.ReadVector());
    }

    /// <summary>A stick drives the same action the keys do, scaled by the binding's direction.</summary>
    [Fact]
    public void AStickAxisScalesItsBindingsDirection()
    {
        var input = new BufferedInputSource();
        var move = Assert.IsType<InputAction>(InputActionsAsset.CreateDefault().Find("Move"));

        input.PushGamepadAxis(0, GamepadAxis.LeftStickY, 0.5f);
        move.Update(input);

        Assert.Equal(0.5f, move.ReadVector().Y, 3);
        Assert.Equal(0f, move.ReadVector().X, 3);
    }

    /// <summary>A disabled map's actions stop following the devices and fall back to rest.</summary>
    [Fact]
    public void ADisabledMapStopsReadingItsDevices()
    {
        var input = new BufferedInputSource();
        var service = new InputActionService(input);
        service.Load(InputActionsAsset.CreateDefault());

        input.PushKeyDown(Key.Space);
        service.Update();
        Assert.True(InputActionsFor(service, "Jump").IsPressed);

        service.SetMapEnabled("Gameplay", false);
        service.Update();
        Assert.False(InputActionsFor(service, "Jump").IsPressed);
    }

    /// <summary>Rebinding writes the next control the player presses into the chosen binding slot.</summary>
    [Fact]
    public void RebindingCapturesTheNextControl()
    {
        var input = new BufferedInputSource();
        var service = new InputActionService(input);
        service.Load(InputActionsAsset.CreateDefault());

        var jump = InputActionsFor(service, "Jump");
        var rebind = service.StartRebind(jump);

        input.PushKeyDown(Key.Enter);
        service.Update();

        Assert.True(rebind.IsComplete);
        Assert.Equal(InputControl.Keyboard(Key.Enter), rebind.Captured);
        Assert.Equal("Keyboard/Enter", jump.Bindings[0].Path);
    }

    /// <summary>Escape ends a rebinding without writing anything, so a player can back out.</summary>
    [Fact]
    public void RebindingCanBeCancelled()
    {
        var input = new BufferedInputSource();
        var service = new InputActionService(input);
        service.Load(InputActionsAsset.CreateDefault());

        var jump = InputActionsFor(service, "Jump");
        var rebind = service.StartRebind(jump);

        input.PushKeyDown(Key.Escape);
        service.Update();

        Assert.True(rebind is { IsComplete: true, WasCancelled: true });
        Assert.Equal("Keyboard/Space", jump.Bindings[0].Path);
    }

    /// <summary>Captured bindings round-trip through the store a game persists them with.</summary>
    [Fact]
    public void OverridesRoundTripThroughTheStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"turian-input-{Guid.NewGuid():N}.json");

        try
        {
            var written = new InputActionService(new BufferedInputSource());
            written.Load(InputActionsAsset.CreateDefault());
            InputActionsFor(written, "Jump").Bindings[0].Path = "Keyboard/Enter";
            Assert.True(InputBindingStore.Save(written, path));

            var restored = new InputActionService(new BufferedInputSource());
            restored.Load(InputActionsAsset.CreateDefault());
            Assert.True(InputBindingStore.Load(restored, path));

            Assert.Equal("Keyboard/Enter", InputActionsFor(restored, "Jump").Bindings[0].Path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>An override naming an action the asset no longer has leaves everything else alone.</summary>
    [Fact]
    public void AStaleOverrideIsIgnored()
    {
        var service = new InputActionService(new BufferedInputSource());
        service.Load(InputActionsAsset.CreateDefault());

        service.ApplyOverrides(new Dictionary<string, string>
        {
            ["Gameplay/Fly/0"] = "Keyboard/F",
            ["Gameplay/Jump/9"] = "Keyboard/G",
            ["Gameplay/Jump/1"] = "Keyboard/Enter",
        });

        var jump = InputActionsFor(service, "Jump");
        Assert.Equal("Keyboard/Space", jump.Bindings[0].Path);
        Assert.Equal("Keyboard/Enter", jump.Bindings[1].Path);
    }

    /// <summary>Two gamepads: naming a player reads only that one, and −1 reads whichever is moving.</summary>
    [Fact]
    public void GamepadsAreAddressedByPlayerSlot()
    {
        var input = new BufferedInputSource();
        input.PushGamepadConnected(0);
        input.PushGamepadConnected(1);
        input.PushGamepadAxis(1, GamepadAxis.LeftStickX, 0.8f);

        Assert.Equal([0, 1], input.ConnectedGamepads);
        Assert.Equal(0f, input.GetGamepadAxis(GamepadAxis.LeftStickX, 0));
        Assert.Equal(0.8f, input.GetGamepadAxis(GamepadAxis.LeftStickX, 1));
        Assert.Equal(0.8f, input.GetGamepadAxis(GamepadAxis.LeftStickX));
    }

    static InputAction Button(string name, params InputControl[] controls)
    {
        var action = new InputAction { Name = name };
        foreach (var control in controls) action.Bindings.Add(InputBinding.For(control));
        return action;
    }

    static InputAction InputActionsFor(InputActionService service, string path) =>
        service.Find(path) ?? throw new InvalidOperationException($"No action '{path}'.");
}
