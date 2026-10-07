namespace Turian.Editor.Core;

public sealed partial class ProjectSession
{
    (string Path, string? Scene)? queuedOpen;
    Task<BackgroundTaskStatus>? startupTask;
    string? startupScene;

    /// <summary>Whether a queued startup project is still preparing its assets and scripts.</summary>
    public bool IsOpening => queuedOpen is not null || startupTask is not null;

    /// <summary>Schedules project startup after the host has rendered its first frame.</summary>
    public void QueueOpen(string projectPath, string? openScene = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        if (IsOpening) throw new InvalidOperationException("A project is already opening.");
        queuedOpen = (projectPath, openScene);
    }

    /// <summary>Starts background import and compilation, then restores documents on the UI thread.</summary>
    public bool TickStartup()
    {
        if (queuedOpen is { } request)
        {
            queuedOpen = null;
            if (!PrepareOpen(request.Path)) return false;
            startupScene = request.Scene;
            var runner = services.GetRequiredService<BackgroundTaskRunner>();
            startupTask = runner.RunAsync(new BackgroundTaskSpec
            {
                Label = "Opening project",
                Locks = EditorLocks.Assets | EditorLocks.Scripts | EditorLocks.Scene | EditorLocks.Project,
                BlocksUi = true,
            }, async (progress, _) =>
            {
                await Task.Run(() => ImportAndCompile(progress)).ConfigureAwait(false);
                progress.Report(1, "Project ready");
            });
        }
        if (startupTask is not { IsCompleted: true } completed) return IsOpening;
        startupTask = null;
        if (completed.GetAwaiter().GetResult() == BackgroundTaskStatus.Completed) CompleteOpen(startupScene);
        return false;
    }

    /// <summary>Completes startup before the host saves and disposes project services.</summary>
    public void FinishStartup()
    {
        TickStartup();
        startupTask?.GetAwaiter().GetResult();
        TickStartup();
    }
}
