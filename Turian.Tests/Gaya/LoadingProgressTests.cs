namespace Turian.Tests;

/// <summary>Verifies informative progress rendering and consistent snapshots across both GUI passes.</summary>
[Collection(SerialTests.Name)]
public sealed class LoadingProgressTests
{
    /// <summary>The blocker tracks the active import phase with real counters, then changes to an indeterminate phase.</summary>
    [Fact]
    public void BlockerFollowsNestedPhaseCountersAndCompletion()
    {
        var tasks = new BackgroundTaskManager();
        var blocker = new BackgroundTaskUiBlocker(tasks);
        Assert.False(blocker.IsBlocked);
        Assert.Null(blocker.Progress);
        Assert.Equal("Working...", blocker.Message);
        var id = tasks.Submit(new BackgroundTaskSpec { Label = "Opening project", BlocksUi = true });
        tasks.Start(id);
        using var progress = tasks.ProgressFor(id);
        using (var importing = progress.BeginChild(BackgroundTaskKind.Import, "Importing assets"))
        using (var file = importing.BeginChild(BackgroundTaskKind.Import, "Reading model"))
        {
            file.Report(0, "bistro.fbx");
            file.Units(7, 10);
            var snapshot = blocker.Progress!;
            Assert.True(blocker.IsBlocked);
            Assert.Equal("Opening project", blocker.Message);
            Assert.Equal("Reading model: bistro.fbx", snapshot.Detail);
            Assert.Equal(0.7f, snapshot.Fraction);
            Assert.Equal("7 / 10", snapshot.Counter);
            Assert.True(snapshot.Elapsed >= TimeSpan.Zero);
        }
        using (progress.BeginChild(BackgroundTaskKind.Compile, "Compiling scripts"))
        {
            Assert.Equal("Compiling scripts", blocker.Progress!.Detail);
            Assert.Null(blocker.Progress.Fraction);
        }
        progress.Report(0.9f, "Restoring scenes");
        Assert.Equal("Restoring scenes", blocker.Progress!.Detail);
        Assert.Equal(0.9f, blocker.Progress.Fraction);
        progress.Finish(true);
        Assert.False(blocker.IsBlocked);
        Assert.Null(blocker.Progress);
    }

    /// <summary>The loading card is centered and remains stable when the task completes between passes.</summary>
    [Fact]
    public void ProgressCardIsCenteredAndKeepsItsFrameSnapshot()
    {
        using var frame = new GayaChromeTests.Frame();
        frame.Blocker.IsBlocked.Returns(true);
        frame.Blocker.Progress.Returns(new UiBlockerProgress("Opening project", "Importing assets: bistro.fbx",
            0.42f, "42 / 100", TimeSpan.FromSeconds(12)));
        frame.Draw(() => frame.Blocker.IsBlocked.Returns(false));
        Assert.Equal(0, frame.Gui.FrameWaitSeconds);
        var card = GayaChromeTests.Descendants(frame.Gui.RootNode!).Single(node => node.Id == "__uiBlocker/card");
        Assert.InRange(card.Rect.X + card.Rect.W / 2, 399, 401);
        Assert.InRange(card.Rect.Y + card.Rect.H / 2, 299, 301);
        Assert.True(card.Rect.H >= 120, $"Card height: {card.Rect.H}");
        Assert.True(card.Rect.W < 800);
        if (Environment.GetEnvironmentVariable("TURIAN_QOL_CAPTURE") is { } directory)
        {
            Directory.CreateDirectory(directory);
            frame.Capture(Path.Combine(directory, "opening-project.png"));
        }
        frame.Draw();
        Assert.DoesNotContain(GayaChromeTests.Descendants(frame.Gui.RootNode!), node => node.Id == "__uiBlocker");
        frame.Blocker.IsBlocked.Returns(true);
        frame.Blocker.Progress.Returns(new UiBlockerProgress("Compiling scripts"));
        frame.Draw();
        Assert.Contains(GayaChromeTests.Descendants(frame.Gui.RootNode!), node => node.Id == "__uiBlocker/card");
    }
}
