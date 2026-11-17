namespace Turian.Editor.Core;

/// <summary>
/// An external editor driven by starting its executable with arguments. A subclass names the editor, finds it and
/// builds the arguments; starting the process and reporting a failed start is shared.
/// </summary>
public abstract class CommandLineEditor : IExternalEditor
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public abstract ExternalEditorCapabilities Capabilities { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<EditorInstallation> Discover();

    /// <summary>Builds the command that opens <paramref name="context"/> in <paramref name="installation"/>.</summary>
    /// <param name="installation">The copy to start.</param>
    /// <param name="context">What to open.</param>
    /// <returns>The command.</returns>
    public abstract EditorCommand BuildCommand(EditorInstallation installation, EditorContext context);

    /// <inheritdoc />
    public Task OpenAsync(EditorInstallation installation, EditorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(context);
        return EditorProcess.StartAsync(BuildCommand(installation, context), DisplayName, cancellationToken);
    }
}
