namespace Turian.Editor.Core;

/// <summary>
/// Opens the open project's generated C# solution, or one of its source files, in the external editor the user
/// chose. The solution is preferred so references and navigation resolve; the project folder stands in while none
/// has been generated.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class CodeOpener(
    SettingsService project,
    ExternalEditorLauncher launcher,
    EditorNotifications notifications,
    ILogger log)
{
    /// <summary>Rewrites the generated solution; replaceable so a failure can be provoked without breaking the disk.</summary>
    internal Func<IAppSettings, ILogger, GeneratedProjectFiles> Regenerate { get; set; } = ProjectRegeneration.Regenerate;

    /// <summary>What to open for the open project, or <c>null</c> when no project is open.</summary>
    /// <param name="file">The source file to open, or <c>null</c> to open only the project.</param>
    /// <param name="line">The 1-based line to place the caret on.</param>
    /// <param name="column">The 1-based column to place the caret on.</param>
    /// <returns>The solution when it exists, the project folder otherwise, and the file's place in either.</returns>
    public EditorContext? ContextFor(string? file = null, int? line = null, int? column = null) =>
        project.Settings is { } settings ? ContextFor(settings, file, line, column) : null;

    /// <summary>What to open for a project: its folder, the generated solution when it exists, and a place in a file.</summary>
    /// <param name="settings">The project.</param>
    /// <param name="file">The source file to open, or <c>null</c> to open only the project.</param>
    /// <param name="line">The 1-based line to place the caret on.</param>
    /// <param name="column">The 1-based column to place the caret on.</param>
    /// <returns>The context; each editor decides from it whether it takes the solution or the folder.</returns>
    public static EditorContext ContextFor(IAppSettings settings, string? file = null, int? line = null, int? column = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var build = settings as IBuildAppSettings ?? (IBuildAppSettings)new BuildAppSettings().Load(settings);
        var solution = ProjectRegeneration.SolutionPath(build);
        return new EditorContext(settings.ProjectAbsoluteDir, File.Exists(solution) ? solution : null, file, line, column);
    }

    /// <summary>Opens the project, or a place in one of its files, in the external editor, as the solution stands on disk.</summary>
    /// <param name="file">The source file to open, or <c>null</c> to open only the project.</param>
    /// <param name="line">The 1-based line to place the caret on.</param>
    /// <param name="column">The 1-based column to place the caret on.</param>
    /// <returns>Whether an editor started; a failure is also posted as a notification, and nothing happens without a project.</returns>
    public async Task<bool> OpenAsync(string? file = null, int? line = null, int? column = null)
    {
        if (ContextFor(file, line, column) is not { } context)
        {
            return false;
        }

        return await launcher.OpenAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Regenerates the C# solution and then opens it in the external editor. When regeneration fails nothing is
    /// opened, so an editor is never shown a solution that no longer matches the project.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting for the editor to confirm it started.</param>
    /// <returns>Whether the editor started on the regenerated solution; every failure is also posted as a notification.</returns>
    public async Task<bool> OpenProjectAsync(CancellationToken cancellationToken = default)
    {
        if (project.Settings is not { } settings)
        {
            return false;
        }

        GeneratedProjectFiles files;
        try
        {
            files = Regenerate(settings, log);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.LogWarning(exception, "Regenerating the C# solution failed; nothing was opened");
            notifications.Post(new EditorNotification(
                $"The C# solution could not be regenerated, so it was not opened: {exception.Message}", IsError: true));
            return false;
        }

        var context = new EditorContext(settings.ProjectAbsoluteDir, files.Solution);
        return await launcher.OpenAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
