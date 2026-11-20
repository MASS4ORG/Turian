namespace Turian.Tests;

/// <summary>Checks pointer interaction with the C# project toolbar action.</summary>
[Collection(SerialTests.Name)]
public sealed class OpenCSharpProjectChromeTests
{
    /// <summary>The toolbar action ignores clicks while unavailable and dispatches when enabled.</summary>
    [Fact]
    public void ToolbarClickDispatchesOnlyWhenEnabled()
    {
        const string commandId = "gaya.turian.openCSharpProject";
        using var frame = new WorkbenchFrame();
        var commands = Substitute.For<ICommandDispatcher>();
        using var services = new ServiceCollection().AddSingleton(commands).AddSingleton<StudioLocalization>()
            .BuildServiceProvider();
        var toolbar = Assert.Single(StudioContributionsTests.Configure().Chrome,
            descriptor => descriptor.Id == "gaya.turian.openCSharpProjectToolbar");
        frame.Chrome.Register(toolbar with { Factory = _ => toolbar.Factory(services) });
        frame.Draw();

        var button = WorkbenchFrame.Descendants(frame.Gui.RootNode!).Single(node => node.Id == "open-csharp-project");
        frame.Input.MousePosition.Returns(button.Rect.Center);
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw();
        commands.DidNotReceive().Execute(Arg.Any<string>());

        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        frame.Draw();
        commands.CanExecute(commandId).Returns(true);
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw(() => commands.DidNotReceive().Execute(Arg.Any<string>()));
        commands.Received(1).Execute(commandId);
    }
}
