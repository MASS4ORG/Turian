namespace Gaya.Tests;

/// <summary>Checks that a panel with a close guard decides whether the dock's close button and the View menu close it.</summary>
[Collection(SerialTests.Name)]
public sealed class PanelCloseGuardTests
{
    sealed class GuardedPanel : IPanel, IPanelCloseGuard
    {
        internal readonly List<Action<bool>> Requests = [];
        internal Action<Action<bool>>? Answer;
        internal Action? OnRender;

        public void Render(Gui gui)
        {
            if (gui.Pass == Pass.Pass2Render)
            {
                var action = OnRender;
                OnRender = null;
                action?.Invoke();
            }
        }

        public void RequestClose(Action<bool> decide)
        {
            Requests.Add(decide);
            Answer?.Invoke(decide);
        }
    }

    sealed class PlainPanel : IPanel
    {
        internal Action? OnRender;
        internal Action<Gui>? EveryFrame;

        public void Render(Gui gui)
        {
            EveryFrame?.Invoke(gui);
            if (gui.Pass == Pass.Pass2Render)
            {
                var action = OnRender;
                OnRender = null;
                action?.Invoke();
            }
        }
    }

    static (WorkbenchFrame Frame, GuardedPanel Guarded) Open(PanelPlacement placement = PanelPlacement.Left)
    {
        var frame = new WorkbenchFrame();
        var guarded = new GuardedPanel();
        frame.Panels.Register(new PanelDescriptor("main", "Main", PanelPlacement.Center, _ => new PlainPanel()));
        frame.Panels.Register(new PanelDescriptor("doc", "Doc", placement, _ => guarded));
        frame.Draw();
        return (frame, guarded);
    }

    /// <summary>A close click removes the panel inside the dock; the workbench puts it back until the guard agrees.</summary>
    [Fact]
    public void DockCloseWaitsForTheGuard()
    {
        var (frame, guarded) = Open();
        using var _ = frame;
        var layout = frame.Workbench.Layout;

        guarded.OnRender = () => layout.Remove("doc");
        frame.Draw();

        Assert.Single(guarded.Requests);
        Assert.True(layout.Contains("doc"));
        Assert.Equal(DockZone.Left, layout.FindLeaf("doc")!.Zone);

        guarded.Requests[0](true);
        Assert.False(layout.Contains("doc"));
    }

    /// <summary>Keeping the panel leaves it where it was, and a later close asks again.</summary>
    [Fact]
    public void RefusedCloseKeepsThePanelAndAsksAgainNextTime()
    {
        var (frame, guarded) = Open();
        using var _ = frame;
        var layout = frame.Workbench.Layout;
        guarded.Answer = static decide => decide(false);

        guarded.OnRender = () => layout.Remove("doc");
        frame.Draw();
        Assert.True(layout.Contains("doc"));

        guarded.OnRender = () => layout.Remove("doc");
        frame.Draw();
        Assert.Equal(2, guarded.Requests.Count);
        Assert.True(layout.Contains("doc"));
    }

    /// <summary>A guard that agrees at once closes the panel without leaving it half open.</summary>
    [Fact]
    public void ImmediateApprovalClosesThePanel()
    {
        var (frame, guarded) = Open();
        using var _ = frame;
        guarded.Answer = static decide => decide(true);

        guarded.OnRender = () => frame.Workbench.Layout.Remove("doc");
        frame.Draw();

        Assert.False(frame.Workbench.Layout.Contains("doc"));
    }

    /// <summary>While a question is pending, closing again does not ask a second time.</summary>
    [Fact]
    public void PendingQuestionIsNotAskedTwice()
    {
        var (frame, guarded) = Open();
        using var _ = frame;
        var layout = frame.Workbench.Layout;

        guarded.OnRender = () => layout.Remove("doc");
        frame.Draw();
        guarded.OnRender = () => layout.Remove("doc");
        frame.Draw();

        Assert.Single(guarded.Requests);
        Assert.True(layout.Contains("doc"));
        guarded.Requests[0](false);
        Assert.True(layout.Contains("doc"));
        guarded.Requests[0](true);
        Assert.True(layout.Contains("doc"));
    }

    /// <summary>The View menu closes through the same question, and a panel without a guard closes at once.</summary>
    [Fact]
    public void TogglingAsksTheGuardAndLeavesOtherPanelsAlone()
    {
        var (frame, guarded) = Open();
        using var _ = frame;
        var layout = frame.Workbench.Layout;

        frame.Workbench.TogglePanel("doc");
        Assert.True(layout.Contains("doc"));
        Assert.Single(guarded.Requests);
        guarded.Requests[0](true);
        Assert.False(layout.Contains("doc"));

        frame.Workbench.TogglePanel("main");
        Assert.False(layout.Contains("main"));
    }

    /// <summary>A guard that throws keeps the panel open and can be asked again; closing a closed panel does nothing.</summary>
    [Fact]
    public void ThrowingGuardKeepsThePanelOpen()
    {
        var (frame, guarded) = Open();
        using var _ = frame;
        var layout = frame.Workbench.Layout;
        guarded.Answer = static _ => throw new InvalidOperationException("boom");

        frame.Workbench.ClosePanel("doc");
        frame.Workbench.ClosePanel("doc");

        Assert.True(layout.Contains("doc"));
        Assert.Equal(2, guarded.Requests.Count);
        frame.Workbench.ClosePanel("missing");
        frame.Workbench.ClosePanel("main");
        Assert.False(layout.Contains("main"));
    }
    /// <summary>A guarded panel parked in a floating window comes back in that window.</summary>
    [Fact]
    public void FloatingPanelStaysFloating()
    {
        var (frame, guarded) = Open(PanelPlacement.Floating);
        using var _ = frame;
        var layout = frame.Workbench.Layout;

        guarded.OnRender = () => layout.Remove("doc");
        frame.Draw();

        Assert.True(layout.Contains("doc"));
        Assert.Single(layout.Floating);
    }

    /// <summary>A focused text input keeps Ctrl+Z to itself: the workbench dispatches no shortcut while one has focus.</summary>
    [Fact]
    public void TextInputKeepsUndoToItself()
    {
        using var frame = new WorkbenchFrame();
        var undone = 0;
        frame.Commands.Register(new CommandDescriptor("test.undo", "Undo", _ => undone++));
        frame.Shortcuts.Add(new KeyBinding("test.undo", KeyboardKey.Z, KeyModifiers.Ctrl));
        var panel = new PlainPanel();
        frame.Panels.Register(new PanelDescriptor("main", "Main", PanelPlacement.Center, _ => panel));
        frame.Draw();
        frame.Input.IsKeyDown(KeyboardKey.LeftControl).Returns(true);
        frame.Input.IsKeyPressed(KeyboardKey.Z).Returns(true);

        frame.Draw();
        Assert.Equal(1, undone);

        panel.EveryFrame = static gui =>
        {
            gui.Focus.RegisterTextInput("probe");
            gui.Focus.RegisterFocusableControl("probe");
            gui.Focus.RequestFocus("probe");
        };
        frame.Draw();
        frame.Draw();
        var focused = undone;
        frame.Draw();
        frame.Draw();
        Assert.Equal(focused, undone);
    }
}
