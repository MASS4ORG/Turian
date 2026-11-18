namespace Turian.Editor.Core;

/// <summary>
/// Opens a project or a file in the editor the user chose, never blocking the caller. Without a chosen editor it
/// hands the file to the operating system's default program and, once, hints that an editor can be configured; a
/// failure to start becomes a notification instead of an exception.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class ExternalEditorLauncher(
    IExternalEditorRegistry registry,
    EditorNotifications notifications,
    ILogger log)
{
    /// <summary>Where the preferences keep the editor choice, named in failure notifications.</summary>
    public const string SettingsPath = "External Tools / External Editor";

    /// <summary>Starts the default handler; replaceable so the fallback can run without a desktop.</summary>
    internal Func<string, Task> OpenWithSystem { get; set; } = OpenWithSystemDefaultAsync;

    int hinted;

    /// <summary>The editor to open things with, or <c>null</c> to use the operating system's default program.</summary>
    public ExternalEditorSelection? Selection { get; set; }

    /// <summary>Opens <paramref name="context"/> and reports what happened.</summary>
    /// <param name="context">What to open.</param>
    /// <param name="cancellationToken">Stops waiting for the editor to confirm it started.</param>
    /// <returns>Whether an editor started; failures are also posted as notifications.</returns>
    public async Task<bool> OpenAsync(EditorContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Selection is not { } selection)
        {
            return await OpenWithDefaultAsync(context).ConfigureAwait(false);
        }

        if (registry.Find(selection.EditorId) is not { } editor)
        {
            return Fail($"The external editor '{selection.EditorId}' is not available. Choose another editor.");
        }

        if (!File.Exists(selection.Executable))
        {
            return Fail($"{editor.DisplayName} was not found at {selection.Executable}. Choose the editor again.");
        }

        try
        {
            await editor.OpenAsync(selection.ToInstallation(editor.DisplayName), context, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException exception)
        {
            log.LogDebug(exception, "Opening {Editor} failed", editor.DisplayName);
            return Fail(exception.Message);
        }
    }

    async Task<bool> OpenWithDefaultAsync(EditorContext context)
    {
        var target = context.File ?? context.Solution ?? context.ProjectDirectory;
        try
        {
            await OpenWithSystem(target).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException
                                              or InvalidOperationException or UnauthorizedAccessException)
        {
            log.LogDebug(exception, "No program opened {Path}", target);
            return Fail($"No program could open {Path.GetFileName(target)}. Choose an external editor.");
        }

        if (Interlocked.Exchange(ref hinted, 1) == 0)
        {
            notifications.Post(new EditorNotification(
                "Opened with the system default program. Choose an external editor to open code in your IDE.",
                SettingsPath: SettingsPath));
        }

        return true;
    }

    bool Fail(string message)
    {
        log.LogWarning("{Message}", message);
        notifications.Post(new EditorNotification(message, IsError: true, SettingsPath: SettingsPath));
        return false;
    }

    static Task OpenWithSystemDefaultAsync(string path) => Task.Run(() =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose());
}
