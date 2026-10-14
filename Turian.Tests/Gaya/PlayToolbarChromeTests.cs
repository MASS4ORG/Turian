namespace Turian.Tests;

/// <summary>Checks the Turian play toolbar contributed to the Gaya application bar.</summary>
[Collection(SerialTests.Name)]
public sealed class PlayToolbarChromeTests
{
    /// <summary>A play-button press dispatches once during rendering and never while building the frame.</summary>
    [Fact]
    public void PlayButtonsDoNotDispatchDuringTheBuildPass()
    {
        using var frame = new WorkbenchFrame();
        var commands = Substitute.For<ICommandDispatcher>();
        commands.CanExecute(Arg.Any<string>()).Returns(true);
        var playMode = new PlayModeService(Substitute.For<IPlaySceneHost>(), null!,
            Substitute.For<IServiceProvider>(), NullLogger.Instance);
        frame.Chrome.Register(new ChromeDescriptor("play", ChromeSlot.MenuBar,
            _ => new PlayToolbarChrome(commands, playMode)));
        frame.Draw();
        var play = WorkbenchFrame.Descendants(frame.Gui.RootNode!).Single(node => node.Id == "play/play");
        frame.Input.MousePosition.Returns(play.Rect.Position + new Vector2(12, 12));
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw(() => commands.DidNotReceive().Execute(Arg.Any<string>()));
        commands.Received(1).Execute("gaya.turian.play");
    }
}
