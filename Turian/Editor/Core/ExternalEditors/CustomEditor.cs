namespace Turian.Editor.Core;

/// <summary>
/// The fallback for an editor no brick covers: the user's own executable and an argument template with the
/// <see cref="EditorArgumentTemplate"/> tokens. It has no installations of its own; one exists once the user has
/// chosen an executable.
/// </summary>
public sealed class CustomEditor : CommandLineEditor
{
    /// <summary>The id a stored preference uses for a custom command.</summary>
    public const string EditorId = "custom";

    /// <summary>The template used until the user writes one: the file when there is one, otherwise the project.</summary>
    public const string DefaultTemplate = "{file}";

    /// <inheritdoc />
    public override string Id => EditorId;

    /// <inheritdoc />
    public override string DisplayName => "Custom";

    /// <inheritdoc />
    public override ExternalEditorCapabilities Capabilities =>
        ExternalEditorCapabilities.OpenProject | ExternalEditorCapabilities.OpenFile
        | ExternalEditorCapabilities.GoToLine;

    /// <summary>The option key that tells Studio the program opens solution files as projects.</summary>
    public const string SolutionAwareKey = "solutionAware";

    /// <summary>The solution-aware switch: off, the program receives the project folder; on, the generated solution.</summary>
    public override IReadOnlyList<ExternalEditorOption> Options { get; } =
    [
        new ExternalEditorOption(SolutionAwareKey, "Solution-aware", ExternalEditorOptionKind.Toggle, "false",
            "Turn on for a program that opens a solution file as a project; otherwise it is given the project folder."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<EditorInstallation> Discover() => [];

    /// <summary>Wraps a chosen executable and template as an installation of the custom editor.</summary>
    /// <param name="executable">The program to start.</param>
    /// <param name="arguments">The argument template, or <c>null</c> for <see cref="DefaultTemplate"/>.</param>
    /// <returns>The installation.</returns>
    public static EditorInstallation Installation(string executable, string? arguments) =>
        new(EditorId, Path.GetFileNameWithoutExtension(executable), executable, Arguments: arguments);

    /// <inheritdoc />
    public override EditorCommand BuildCommand(EditorInstallation installation, EditorContext context)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(context);
        var template = string.IsNullOrWhiteSpace(installation.Arguments) ? DefaultTemplate : installation.Arguments;
        var solutionAware = string.Equals(Options[0].ValueIn(installation.Options), bool.TrueString,
            StringComparison.OrdinalIgnoreCase);
        var arguments = EditorArgumentTemplate.Expand(template, context, solutionAware);
        // A template that only names a file has nothing to open for the project alone.
        return new EditorCommand(installation.Executable,
            arguments.Count > 0 ? arguments : [context.Target(solutionAware)]);
    }
}
