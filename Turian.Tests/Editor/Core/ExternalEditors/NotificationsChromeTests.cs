namespace Turian.Tests;

/// <summary>Notifications reach the user as toasts naming where to fix the problem.</summary>
public sealed class NotificationsChromeTests
{
    /// <summary>The toast carries the settings path when the notification has one.</summary>
    [Fact]
    public void TextNamesTheSettingsPath()
    {
        Assert.Equal("Rider was not found. (External Tools / External Editor)", Gaya.Plugin.Turian.NotificationsChrome.Text(
            new EditorNotification("Rider was not found.", IsError: true, SettingsPath: "External Tools / External Editor")));
        Assert.Equal("Done.", Gaya.Plugin.Turian.NotificationsChrome.Text(new EditorNotification("Done.")));
    }

    /// <summary>Notifications leave the queue in the order they were posted, once each.</summary>
    [Fact]
    public void QueueDeliversInOrderOnce()
    {
        var notifications = new EditorNotifications();
        notifications.Post(new EditorNotification("first"));
        notifications.Post(new EditorNotification("second"));

        Assert.True(notifications.TryTake(out var first));
        Assert.True(notifications.TryTake(out var second));
        Assert.False(notifications.TryTake(out _));
        Assert.Equal(["first", "second"], [first.Message, second.Message]);
    }
}
