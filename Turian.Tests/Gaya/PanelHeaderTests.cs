namespace Turian.Tests;

/// <summary>Checks that Gaya offers header space to the same panel instance that renders its body.</summary>
[Collection(SerialTests.Name)]
public sealed class PanelHeaderTests
{
    sealed class Panel : IPanel, IDisposable
    {
        internal readonly List<(Pass Pass, PanelHeaderContext Context)> Headers = [];
        internal int Bodies;
        internal int Disposals;
        internal bool ThrowInHeader;

        /// <inheritdoc />
        public void Render(Gui gui) => Bodies++;

        /// <inheritdoc />
        public void RenderHeader(Gui gui, PanelHeaderContext context)
        {
            Headers.Add((gui.Pass, context));
            if (ThrowInHeader) throw new InvalidOperationException("Header failed");
            using (gui.Node(24, -1, $"{context.PanelId}/lock").ExpandHeight().Enter())
                gui.DrawText("L");
            using (gui.Node().Expand().Direction(Axis.Horizontal).Enter())
                gui.DrawText("Mini widget");
        }

        /// <inheritdoc />
        public void Dispose() => Disposals++;
    }

    /// <summary>One factory supplies body and header, with space and host context available in both passes.</summary>
    [Fact]
    public void HeaderIsAutomaticAndSharesThePanelInstance()
    {
        using var frame = new GayaChromeTests.Frame();
        var panel = new Panel();
        var factories = 0;
        frame.Panels.Register(new PanelDescriptor("custom", "Custom", PanelPlacement.Center, _ =>
        {
            factories++;
            return panel;
        }));
        frame.Draw();
        Assert.Equal(1, factories);
        Assert.Equal(2, panel.Bodies);
        Assert.Equal(new[] { Pass.Pass1Build, Pass.Pass2Render }, panel.Headers.Select(item => item.Pass));
        var context = panel.Headers[^1].Context;
        Assert.Equal("custom", context.PanelId);
        Assert.Equal("Custom", context.Title);
        Assert.Same(frame.Services, context.Services);
        Assert.True(context.AvailableSpace.W > 24);
        Assert.True(context.AvailableSpace.H > 0);
        Assert.False(context.IsFocused);
        frame.Input.MousePosition.Returns(context.AvailableSpace.Center);
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw();
        Assert.True(panel.Headers[^1].Context.IsFocused);
    }

    /// <summary>Only the active tab draws its header, and removing its panel releases that instance.</summary>
    [Fact]
    public void HeaderFollowsActivationAndPanelLifetime()
    {
        using var frame = new GayaChromeTests.Frame();
        var first = new Panel();
        var second = new Panel();
        frame.Panels.Register(new PanelDescriptor("first", "First", PanelPlacement.Center, _ => first));
        frame.Panels.Register(new PanelDescriptor("second", "Second", PanelPlacement.Center, _ => second));
        frame.Draw();
        var active = frame.Workbench.Layout.FindLeaf("first")!.ActivePanelId;
        var shown = active == "first" ? first : second;
        var hidden = active == "first" ? second : first;
        Assert.Equal(2, shown.Headers.Count);
        Assert.Empty(hidden.Headers);
        frame.Workbench.Layout.Activate(active == "first" ? "second" : "first");
        frame.Draw();
        Assert.Equal(2, hidden.Headers.Count);
        frame.Panels.Remove("first");
        frame.Draw();
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, second.Disposals);
    }

    /// <summary>A header failure is contained without suppressing the panel's body or other panels.</summary>
    [Fact]
    public void HeaderFailureDoesNotPreventBodyRendering()
    {
        using var frame = new GayaChromeTests.Frame();
        var panel = new Panel { ThrowInHeader = true };
        frame.Panels.Register(new PanelDescriptor("broken", "Broken", PanelPlacement.Center, _ => panel));
        frame.Draw();
        Assert.Equal(2, panel.Bodies);
        Assert.Equal(2, panel.Headers.Count);
    }

    /// <summary>Replacing a contribution releases its old panel and supplies the new instance to body and header.</summary>
    [Fact]
    public void ReplacingAPanelReplacesItsHeaderInstance()
    {
        using var frame = new GayaChromeTests.Frame();
        var old = new Panel();
        var replacement = new Panel();
        frame.Panels.Register(new PanelDescriptor("live", "Live", PanelPlacement.Center, _ => old));
        frame.Draw();
        frame.Panels.Register(new PanelDescriptor("live", "Recompiled", PanelPlacement.Center, _ => replacement));
        frame.Draw();
        Assert.Equal(1, old.Disposals);
        Assert.Equal(2, old.Headers.Count);
        Assert.Equal(2, replacement.Bodies);
        Assert.Equal(2, replacement.Headers.Count);
        Assert.Equal("Recompiled", replacement.Headers[^1].Context.Title);
    }
}
