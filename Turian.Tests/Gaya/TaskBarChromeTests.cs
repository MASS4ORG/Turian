namespace Turian.Tests;

/// <summary>Checks redraw deadlines for the task bar without opening a window.</summary>
[Collection(SerialTests.Name)]
public sealed class TaskBarChromeTests
{
    /// <summary>Only unfinished tasks schedule refreshes for the elapsed-time display.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ActiveTasksScheduleElapsedTimeRefresh(bool hasTask, bool completed)
    {
        using var frame = new GayaChromeTests.Frame();
        var tasks = new BackgroundTaskManager();
        var chrome = new TaskBarChrome(tasks);
        Assert.Throws<ArgumentNullException>(() => chrome.Render(null!));
        frame.Chrome.Register(new ChromeDescriptor("tasks", ChromeSlot.StatusBar, _ => chrome,
            Height: TaskBarChrome.Height));
        if (hasTask)
        {
            var id = tasks.Begin(BackgroundTaskKind.Import, "Importing assets");
            tasks.SetProgress(id, 0.5f);
            if (completed) tasks.Complete(id);
        }
        frame.Draw();
        if (hasTask && !completed) Assert.InRange(frame.Gui.FrameWaitSeconds, 0, 0.1);
        else Assert.Equal(double.PositiveInfinity, frame.Gui.FrameWaitSeconds);
    }
}
