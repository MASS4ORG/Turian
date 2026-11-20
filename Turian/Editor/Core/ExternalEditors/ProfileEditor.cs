namespace Turian.Editor.Core;

/// <summary>
/// An external editor driven entirely by an <see cref="EditorLaunchProfile"/>: the profile says what to open, with
/// which arguments and which window switches, and this class finds the program and starts it.
/// </summary>
/// <param name="profile">What to launch and how.</param>
public sealed class ProfileEditor(EditorLaunchProfile profile) : CommandLineEditor
{
    /// <summary>Finds the program from names and folders; replaceable so discovery can run without the real machine.</summary>
    internal Func<IEnumerable<string>, IEnumerable<string>, string?> Locate { get; set; } =
        static (names, directories) => ExecutableLocator.Find(names, directories);

    /// <summary>The profile this editor runs.</summary>
    public EditorLaunchProfile Profile => profile;

    /// <inheritdoc />
    public override string Id => profile.Id;

    /// <inheritdoc />
    public override string DisplayName => profile.DisplayName;

    /// <inheritdoc />
    public override ExternalEditorCapabilities Capabilities => profile.Capabilities;

    /// <summary>The window choice, offered only for the policies the editor's command line can ask for.</summary>
    public override IReadOnlyList<ExternalEditorOption> Options => profile.SupportedWindows is { Count: > 0 } supported
        ?
        [
            new ExternalEditorOption(ExternalEditorWindows.OptionKey, "Window", ExternalEditorOptionKind.Choice,
                ExternalEditorWindows.ToValue(supported.Contains(ExternalEditorWindow.NewUnlessOpen)
                    ? ExternalEditorWindow.NewUnlessOpen
                    : supported[0]),
                profile.Rule(ExternalEditorWindow.NewUnlessOpen).Behavior,
                [.. supported.Select(static window => new ExternalEditorOptionChoice(
                    ExternalEditorWindows.ToValue(window), ExternalEditorWindows.Label(window)))]),
        ]
        : [];

    /// <inheritdoc />
    public override bool Recognizes(string executable) =>
        profile.Discovery.Names.Contains(Path.GetFileNameWithoutExtension(executable), StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override IReadOnlyList<EditorInstallation> Discover() =>
        Locate(profile.Discovery.Names, profile.Discovery.Directories) is { } program
            ? [new EditorInstallation(Id, DisplayName, program)]
            : [];

    /// <inheritdoc />
    public override EditorCommand BuildCommand(EditorInstallation installation, EditorContext context)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(context);

        var window = ExternalEditorWindows.Parse(
            installation.Options is not null && installation.Options.TryGetValue(ExternalEditorWindows.OptionKey, out var stored)
                ? stored
                : null);
        var opens = context.File is null ? profile.ProjectArguments : profile.FileArguments;
        List<string> arguments =
        [
            .. EditorArgumentTemplate.Expand(profile.WindowArguments(window), context, profile.SolutionAware),
            .. EditorArgumentTemplate.Expand(opens, context, profile.SolutionAware),
        ];
        return new EditorCommand(installation.Executable, arguments);
    }
}
