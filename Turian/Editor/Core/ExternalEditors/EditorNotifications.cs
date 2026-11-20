using System.Diagnostics.CodeAnalysis;

namespace Turian.Editor.Core;

/// <summary>A message for the user that a shell shows without blocking, such as a toast.</summary>
/// <param name="Message">What happened, in one sentence.</param>
/// <param name="IsError">Whether the message reports a failure rather than a hint.</param>
/// <param name="SettingsPath">
/// Where in the preferences the user can fix it, such as <c>External Tools / External Editor</c>; <c>null</c> when
/// there is none.
/// </param>
public sealed record EditorNotification(string Message, bool IsError = false, string? SettingsPath = null);

/// <summary>
/// Collects the notifications services raise, from any thread, until the shell draws them. Services never touch the
/// UI; the shell drains the queue on its own thread.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class EditorNotifications
{
    readonly ConcurrentQueue<EditorNotification> pending = new();

    /// <summary>Queues a notification.</summary>
    /// <param name="notification">The notification to show.</param>
    public void Post(EditorNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        pending.Enqueue(notification);
    }

    /// <summary>Takes the oldest notification that has not been shown yet.</summary>
    /// <param name="notification">The notification, when one was waiting.</param>
    /// <returns><c>true</c> when a notification was taken.</returns>
    public bool TryTake([NotNullWhen(true)] out EditorNotification? notification) =>
        pending.TryDequeue(out notification);
}
