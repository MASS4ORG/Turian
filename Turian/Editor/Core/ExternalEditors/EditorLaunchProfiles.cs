namespace Turian.Editor.Core;

/// <summary>
/// The launch profiles Studio ships for the editors people use with C# projects. They are the defaults: a brick that
/// registers an editor with the same id replaces one, and a brick can add editors of its own.
/// </summary>
public static class EditorLaunchProfiles
{
    const ExternalEditorCapabilities ProjectAndFile = ExternalEditorCapabilities.OpenProject | ExternalEditorCapabilities.OpenFile;

    /// <summary>Visual Studio Code: a folder-based editor whose command line chooses the window.</summary>
    public static EditorLaunchProfile VisualStudioCode { get; } = new(
        "vscode", "Visual Studio Code", SolutionAware: false,
        ProjectAndFile | ExternalEditorCapabilities.GoToLine | ExternalEditorCapabilities.ReuseInstance,
        new EditorDiscovery(["code"], Directories(
            linux: [Path.Combine(Home, ".local", "bin"), "/usr/local/bin", "/usr/bin", "/snap/bin"],
            mac: ["/Applications/Visual Studio Code.app/Contents/Resources/app/bin", "/usr/local/bin"],
            windows: [Path.Combine(Local, "Programs", "Microsoft VS Code", "bin"),
                Path.Combine(ProgramFiles, "Microsoft VS Code", "bin")])),
        ProjectArguments: "{target}",
        FileArguments: "{target} --goto {file}:{line}:{column}",
        NewWindow: new("--new-window", "Opens a new window even when the project is open in another one."),
        Reuse: new("--reuse-window", "Replaces the folder shown in the window VS Code used last."),
        NewUnlessOpen: new(string.Empty,
            "VS Code shows the window that already has this folder open, and opens a new window otherwise."));

    /// <summary>Zed: a folder-based editor whose command line can force a window but cannot ask whether the project is open.</summary>
    public static EditorLaunchProfile Zed { get; } = new(
        "zed", "Zed", SolutionAware: false,
        ProjectAndFile | ExternalEditorCapabilities.GoToLine | ExternalEditorCapabilities.ReuseInstance,
        new EditorDiscovery(["zed", "zeditor", "zed-editor"], Directories(
            linux: [Path.Combine(Home, ".local", "bin"), Path.Combine(Home, ".local", "zed.app", "bin"),
                "/usr/local/bin", "/usr/bin"],
            mac: ["/usr/local/bin", "/opt/homebrew/bin", "/Applications/Zed.app/Contents/MacOS"],
            windows: [Path.Combine(Local, "Programs", "Zed", "bin")])),
        ProjectArguments: "{target}",
        FileArguments: "{target} {file}:{line}:{column}",
        NewWindow: new("--new", "Opens the project in a new workspace even when it is open elsewhere."),
        Reuse: new("--reuse", "Replaces the workspace of an existing Zed window."),
        NewUnlessOpen: new(string.Empty,
            "Studio cannot ask Zed whether the project is already open, so this leaves the choice to Zed's own default."));

    /// <summary>JetBrains Rider: a solution-aware editor that decides for itself which window to use.</summary>
    public static EditorLaunchProfile Rider { get; } = new(
        "rider", "JetBrains Rider", SolutionAware: true,
        ProjectAndFile | ExternalEditorCapabilities.GoToLine | ExternalEditorCapabilities.ReuseInstance,
        new EditorDiscovery(["rider", "rider.sh", "rider64"], Directories(
            linux: ["/snap/bin", Path.Combine(Home, ".local", "share", "JetBrains", "Toolbox", "scripts"),
                "/usr/local/bin", "/opt/rider/bin"],
            mac: ["/usr/local/bin", "/Applications/Rider.app/Contents/MacOS"],
            windows: [Path.Combine(Local, "JetBrains", "Toolbox", "scripts"), Path.Combine(Local, "Programs", "Rider", "bin")])),
        ProjectArguments: "{target}",
        FileArguments: "{target} --line {line} --column {column} {file}",
        NewWindow: NoSwitch("Rider"),
        Reuse: NoSwitch("Rider"),
        NewUnlessOpen: NoSwitch("Rider"));

    /// <summary>Visual Studio: a solution-aware, Windows-only editor with no line or window switches.</summary>
    public static EditorLaunchProfile VisualStudio { get; } = new(
        "visualstudio", "Visual Studio", SolutionAware: true,
        ProjectAndFile | ExternalEditorCapabilities.ReuseInstance,
        new EditorDiscovery(["devenv"], VisualStudioDirectories()),
        ProjectArguments: "{target}",
        FileArguments: "/Edit {file}",
        NewWindow: NoSwitch("Visual Studio"),
        Reuse: NoSwitch("Visual Studio"),
        NewUnlessOpen: NoSwitch("Visual Studio"));

    /// <summary>Every shipped profile, in the order the preferences list them.</summary>
    public static IReadOnlyList<EditorLaunchProfile> BuiltIn { get; } = [Rider, VisualStudio, VisualStudioCode, Zed];

    static EditorWindowRule NoSwitch(string editor) => new(null,
        $"{editor}'s command line cannot choose the window, so {editor} decides: " +
        "a project that is already open is brought forward, otherwise its own settings apply.");

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    static string[] Directories(string[] linux, string[] mac, string[] windows) =>
        OperatingSystem.IsWindows() ? windows : OperatingSystem.IsMacOS() ? mac : linux;

    static string[] VisualStudioDirectories() => VisualStudioDirectories(ProgramFiles, OperatingSystem.IsWindows());

    /// <summary>
    /// The folders Visual Studio's <c>devenv</c> can be in. Each release and edition has its own folder, so they are
    /// read from disk, newest release first; there are none off Windows.
    /// </summary>
    /// <param name="programFiles">The Program Files folder that holds <c>Microsoft Visual Studio</c>.</param>
    /// <param name="windows">Whether this is Windows.</param>
    /// <returns>The <c>Common7\IDE</c> folders of every installed edition.</returns>
    internal static string[] VisualStudioDirectories(string programFiles, bool windows)
    {
        var root = Path.Combine(programFiles, "Microsoft Visual Studio");
        if (!windows || !Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.EnumerateDirectories(root)
            .OrderByDescending(static release => Path.GetFileName(release), StringComparer.Ordinal)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(static edition => Path.Combine(edition, "Common7", "IDE"))];
    }
}
