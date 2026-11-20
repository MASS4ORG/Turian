namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws the notifications editor services raise, such as an external editor that failed to start, as toasts. It
/// lives in the overlay slot so toasts show whichever panel is in front.
/// </summary>
sealed class NotificationsChrome(EditorNotifications notifications) : IChromeItem
{
    static readonly ToastOptions Options = new() { Duration = 7f, MaxWidth = 480f };

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);
        while (notifications.TryTake(out var notification))
        {
            gui.Toast(Text(notification), Options);
        }
    }

    /// <summary>The toast text: the message and, when there is one, where in the preferences to fix it.</summary>
    /// <param name="notification">The notification to show.</param>
    /// <returns>The text.</returns>
    public static string Text(EditorNotification notification) => notification.SettingsPath is { } path
        ? $"{notification.Message} ({path})"
        : notification.Message;
}
