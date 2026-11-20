namespace Turian.Editor.Core;

/// <summary>How an editor's command line can ask for one <see cref="ExternalEditorWindow"/> policy.</summary>
/// <param name="Arguments">
/// The switches to add, as an <see cref="EditorArgumentTemplate"/>; empty when the editor already does this by
/// default; <c>null</c> when its command line has no way to ask for it, so the policy is not offered.
/// </param>
/// <param name="Behavior">What the editor really does, in one sentence; shown in the preferences and in the documentation.</param>
public sealed record EditorWindowRule(string? Arguments, string Behavior)
{
    /// <summary>Whether the editor's command line can ask for this policy.</summary>
    public bool IsSupported => Arguments is not null;
}

/// <summary>Where to look for an editor's program.</summary>
/// <param name="Names">File names without extension, in order of preference.</param>
/// <param name="Directories">The folders to search after <c>PATH</c> on this operating system.</param>
public sealed record EditorDiscovery(IReadOnlyList<string> Names, IReadOnlyList<string> Directories);

/// <summary>
/// Everything Studio knows about launching one editor: whether it opens solutions or folders, the arguments for a
/// project and for a file, and what each window policy does. Profiles are data, so an editor brick can replace one
/// by registering an editor with the same id.
/// </summary>
/// <param name="Id">The stable id a stored preference refers to.</param>
/// <param name="DisplayName">The editor's product name.</param>
/// <param name="SolutionAware">
/// Whether the editor opens a solution file as a project. A solution-aware editor receives the generated solution; any
/// other receives the project folder.
/// </param>
/// <param name="Capabilities">What the command line can do.</param>
/// <param name="Discovery">Where to look for the program.</param>
/// <param name="ProjectArguments">The arguments that open the project alone, using the <see cref="EditorArgumentTemplate"/> tokens.</param>
/// <param name="FileArguments">The arguments that open a file inside the project, at a line and column when it can.</param>
/// <param name="NewWindow">How to ask for <see cref="ExternalEditorWindow.NewWindow"/>.</param>
/// <param name="Reuse">How to ask for <see cref="ExternalEditorWindow.Reuse"/>.</param>
/// <param name="NewUnlessOpen">How to ask for <see cref="ExternalEditorWindow.NewUnlessOpen"/>.</param>
public sealed record EditorLaunchProfile(
    string Id,
    string DisplayName,
    bool SolutionAware,
    ExternalEditorCapabilities Capabilities,
    EditorDiscovery Discovery,
    string ProjectArguments,
    string FileArguments,
    EditorWindowRule NewWindow,
    EditorWindowRule Reuse,
    EditorWindowRule NewUnlessOpen)
{
    /// <summary>The rule for a window policy.</summary>
    /// <param name="window">The policy.</param>
    /// <returns>Its rule.</returns>
    public EditorWindowRule Rule(ExternalEditorWindow window) => window switch
    {
        ExternalEditorWindow.NewWindow => NewWindow,
        ExternalEditorWindow.Reuse => Reuse,
        _ => NewUnlessOpen,
    };

    /// <summary>The policies the editor's command line can ask for, in the order the preferences list them.</summary>
    public IReadOnlyList<ExternalEditorWindow> SupportedWindows =>
        [.. Enum.GetValues<ExternalEditorWindow>().Where(window => Rule(window).IsSupported)];

    /// <summary>
    /// The switches that apply for a stored policy. A policy the editor cannot ask for falls back to its default
    /// behaviour, never to a guess.
    /// </summary>
    /// <param name="window">The stored policy.</param>
    /// <returns>The switch template; empty when none apply.</returns>
    public string WindowArguments(ExternalEditorWindow window) =>
        (Rule(window).IsSupported ? Rule(window) : NewUnlessOpen).Arguments ?? string.Empty;
}
