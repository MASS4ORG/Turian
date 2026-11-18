namespace Turian.Editor.Core;

/// <summary>
/// The one contract Studio uses to hand a project or a file to a third-party editor. Everything specific to an editor
/// or an operating system, such as where it installs and how it takes a line number, stays behind it.
/// </summary>
public interface IExternalEditor
{
    /// <summary>The stable id a stored preference refers to.</summary>
    string Id { get; }

    /// <summary>The editor's product name.</summary>
    string DisplayName { get; }

    /// <summary>What the editor can do.</summary>
    ExternalEditorCapabilities Capabilities { get; }

    /// <summary>The settings this editor adds to the preferences; none unless it overrides this.</summary>
    IReadOnlyList<ExternalEditorOption> Options => [];

    /// <summary>Whether a program the user browsed to is a copy of this editor, so it can be launched as one.</summary>
    /// <param name="executable">The full path of the program.</param>
    bool Recognizes(string executable) => false;

    /// <summary>Finds the copies of this editor on the machine.</summary>
    /// <returns>The installations, newest first; empty when the editor is absent or not available on this OS.</returns>
    IReadOnlyList<EditorInstallation> Discover();

    /// <summary>Opens the project, or the file inside it, and returns once the editor has been started.</summary>
    /// <param name="installation">The copy to start, one this editor discovered or one built from a stored path.</param>
    /// <param name="context">What to open.</param>
    /// <param name="cancellationToken">Cancels waiting for the launch to be confirmed.</param>
    /// <exception cref="InvalidOperationException">The editor could not be started; the message says why.</exception>
    Task OpenAsync(EditorInstallation installation, EditorContext context, CancellationToken cancellationToken);
}
