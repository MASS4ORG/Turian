namespace Turian.Tests;

/// <summary>
/// Each shipped profile turns what Studio wants opened into the arguments the editor's command line takes: which target
/// it gets, which window switches apply, and how a file and line are named. The editors' real windows are not tested here.
/// </summary>
public sealed class EditorLaunchProfileTests
{
    static readonly string Project = Path.Combine(Path.DirectorySeparatorChar.ToString(), "work", "My Game ü");
    static readonly string Solution = Path.Combine(Project, ".Cache", "My Game ü.slnx");
    static readonly string Script = Path.Combine(Project, "Assets", "Player Ü.cs");

    static EditorContext Open(bool solution = true) => new(Project, solution ? Solution : null);

    static EditorContext OpenFile(bool solution = true) => new(Project, solution ? Solution : null, Script, 4, 9);

    static ProfileEditor Editor(string id) =>
        new(EditorLaunchProfiles.BuiltIn.Single(profile => profile.Id == id));

    static IReadOnlyList<string> Arguments(string id, EditorContext context, string? window = null)
    {
        var installation = new EditorInstallation(id, id, "/opt/editor",
            Options: window is null ? null : new Dictionary<string, string> { [ExternalEditorWindows.OptionKey] = window });
        var command = Editor(id).BuildCommand(installation, context);
        Assert.Equal("/opt/editor", command.FileName);
        return command.Arguments;
    }

    /// <summary>The target is the solution only for a solution-aware editor that has one; every other case gets the project folder.</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void TargetIsTheSolutionOnlyForASolutionAwareEditorThatHasOne(bool aware, bool hasSolution, bool expectsSolution)
    {
        var target = Open(hasSolution).Target(aware);

        Assert.Equal(expectsSolution ? Solution : Project, target);
    }

    /// <summary>A folder-based editor is never handed the solution file, so it cannot add it to a window of another project.</summary>
    [Theory]
    [InlineData("zed")]
    [InlineData("vscode")]
    public void FolderEditorsNeverReceiveTheSolution(string id)
    {
        foreach (var window in new[] { null, "new", "reuse", "new-unless-open" })
        {
            Assert.DoesNotContain(Arguments(id, Open(), window), static argument => argument.EndsWith(".slnx", StringComparison.Ordinal));
            Assert.DoesNotContain(Arguments(id, OpenFile(), window), static argument => argument.EndsWith(".slnx", StringComparison.Ordinal));
        }

        Assert.Equal([Project], Arguments(id, Open()));
    }

    /// <summary>VS Code opens the folder, names a file as <c>--goto path:line:column</c>, and takes its window switches first.</summary>
    [Fact]
    public void VisualStudioCodeArguments()
    {
        Assert.Equal([Project], Arguments("vscode", Open()));
        Assert.Equal([Project, "--goto", $"{Script}:4:9"], Arguments("vscode", OpenFile()));
        Assert.Equal(["--new-window", Project], Arguments("vscode", Open(), "new"));
        Assert.Equal(["--reuse-window", Project, "--goto", $"{Script}:4:9"], Arguments("vscode", OpenFile(), "reuse"));
        Assert.Equal([Project], Arguments("vscode", Open(), "new-unless-open"));
    }

    /// <summary>Zed opens the folder, names a file as <c>path:line:column</c>, and takes its window switches first.</summary>
    [Fact]
    public void ZedArguments()
    {
        Assert.Equal([Project], Arguments("zed", Open()));
        Assert.Equal([Project, $"{Script}:4:9"], Arguments("zed", OpenFile()));
        Assert.Equal(["--new", Project], Arguments("zed", Open(), "new"));
        Assert.Equal(["--reuse", Project, $"{Script}:4:9"], Arguments("zed", OpenFile(), "reuse"));
    }

    /// <summary>Rider receives the solution when there is one and the folder when there is none, with the file's line and column as switches.</summary>
    [Fact]
    public void RiderArguments()
    {
        Assert.Equal([Solution], Arguments("rider", Open()));
        Assert.Equal([Project], Arguments("rider", Open(solution: false)));
        Assert.Equal([Solution, "--line", "4", "--column", "9", Script], Arguments("rider", OpenFile()));
        Assert.Equal([Project, "--line", "4", "--column", "9", Script], Arguments("rider", OpenFile(solution: false)));
    }

    /// <summary>Visual Studio receives the solution to open the project and <c>/Edit</c> for a file, which takes no line.</summary>
    [Fact]
    public void VisualStudioArguments()
    {
        Assert.Equal([Solution], Arguments("visualstudio", Open()));
        Assert.Equal([Project], Arguments("visualstudio", Open(solution: false)));
        Assert.Equal(["/Edit", Script], Arguments("visualstudio", OpenFile()));
    }

    /// <summary>Spaces and non-ASCII characters in the paths stay inside one argument for every profile.</summary>
    [Fact]
    public void PathsStayOneArgument()
    {
        foreach (var profile in EditorLaunchProfiles.BuiltIn)
        {
            var arguments = Arguments(profile.Id, OpenFile());

            Assert.Contains(arguments, static argument => argument.Contains("My Game ü", StringComparison.Ordinal));
            Assert.All(arguments, static argument => Assert.Equal(argument.Trim(), argument));
        }
    }

    /// <summary>A window the editor's command line cannot ask for adds no switches, whatever is stored; an unknown value means the default.</summary>
    [Theory]
    [InlineData("rider", "new")]
    [InlineData("rider", "reuse")]
    [InlineData("visualstudio", "new")]
    [InlineData("vscode", "bogus")]
    [InlineData("zed", "")]
    public void UnsupportedOrUnknownWindowAddsNoSwitches(string id, string window)
    {
        Assert.Equal(Arguments(id, Open()), Arguments(id, Open(), "new-unless-open"));
        Assert.DoesNotContain(Arguments(id, Open(), window), static argument => argument.StartsWith("--new", StringComparison.Ordinal)
                                                                              || argument.StartsWith("--reuse", StringComparison.Ordinal));
    }

    /// <summary>The window choice is offered only for editors whose command line can ask for a window, with the default first described.</summary>
    [Fact]
    public void OnlyEditorsThatCanChooseAWindowOfferTheOption()
    {
        Assert.Empty(Editor("rider").Options);
        Assert.Empty(Editor("visualstudio").Options);
        foreach (var id in new[] { "vscode", "zed" })
        {
            var option = Assert.Single(Editor(id).Options);
            Assert.Equal(ExternalEditorWindows.OptionKey, option.Key);
            Assert.Equal(ExternalEditorOptionKind.Choice, option.Kind);
            Assert.Equal("new-unless-open", option.DefaultValue);
            Assert.Equal(["new-unless-open", "new", "reuse"], option.Choices!.Select(static choice => choice.Value));
            Assert.Equal("new-unless-open", option.ValueIn(null));
            Assert.Equal("reuse", option.ValueIn(new Dictionary<string, string> { ["window"] = "reuse" }));
            Assert.Equal("new-unless-open", option.ValueIn(new Dictionary<string, string> { ["window"] = "bogus" }));
        }
    }

    /// <summary>Every profile documents what each window policy does, uses only known tokens, and has an unambiguous id.</summary>
    [Fact]
    public void ProfilesAreDocumentedAndValid()
    {
        Assert.Equal(EditorLaunchProfiles.BuiltIn.Count, EditorLaunchProfiles.BuiltIn.Select(static p => p.Id).Distinct().Count());
        foreach (var profile in EditorLaunchProfiles.BuiltIn)
        {
            foreach (var window in Enum.GetValues<ExternalEditorWindow>())
            {
                Assert.False(string.IsNullOrWhiteSpace(profile.Rule(window).Behavior), $"{profile.Id} {window}");
                Assert.True(EditorArgumentTemplate.IsValid(profile.Rule(window).Arguments, out _), $"{profile.Id} {window}");
            }

            Assert.True(EditorArgumentTemplate.IsValid(profile.ProjectArguments, out _), profile.Id);
            Assert.True(EditorArgumentTemplate.IsValid(profile.FileArguments, out _), profile.Id);
            Assert.NotEmpty(profile.Discovery.Names);
        }
    }

    /// <summary>The solution-aware defaults are the ones the profiles claim: Rider and Visual Studio, not Zed or VS Code.</summary>
    [Fact]
    public void SolutionAwarenessDefaults()
    {
        Assert.Equal(["rider", "visualstudio"],
            EditorLaunchProfiles.BuiltIn.Where(static p => p.SolutionAware).Select(static p => p.Id).Order());
    }

    /// <summary>The editor reports its profile's identity and what its command line can do.</summary>
    [Fact]
    public void EditorMirrorsItsProfile()
    {
        foreach (var profile in EditorLaunchProfiles.BuiltIn)
        {
            var editor = new ProfileEditor(profile);

            Assert.Equal(profile.Id, editor.Id);
            Assert.Equal(profile.DisplayName, editor.DisplayName);
            Assert.Equal(profile.Capabilities, editor.Capabilities);
            Assert.Same(profile, editor.Profile);
        }
    }

    /// <summary>An editor built on <see cref="CommandLineEditor"/> offers no options and recognises no program unless it says so.</summary>
    [Fact]
    public void CommandLineEditorDefaultsOfferNothing()
    {
        var editor = new PlainEditor();

        Assert.Empty(editor.Options);
        Assert.False(editor.Recognizes("/usr/bin/plain"));
    }

    sealed class PlainEditor : CommandLineEditor
    {
        public override string Id => "plain";

        public override string DisplayName => "Plain";

        public override ExternalEditorCapabilities Capabilities => ExternalEditorCapabilities.None;

        public override IReadOnlyList<EditorInstallation> Discover() => [];

        public override EditorCommand BuildCommand(EditorInstallation installation, EditorContext context) =>
            new(installation.Executable, []);
    }

    /// <summary>Visual Studio's folders are read from every release and edition, newest release first, and only on Windows.</summary>
    [Fact]
    public void VisualStudioFoldersAreReadNewestReleaseFirst()
    {
        var programFiles = Directory.CreateTempSubdirectory("turian-vs-").FullName;
        try
        {
            string Ide(string release, string edition) => Directory
                .CreateDirectory(Path.Combine(programFiles, "Microsoft Visual Studio", release, edition, "Common7", "IDE")).FullName;
            var old = Ide("18", "Insiders");
            var first = Ide("2022", "Community");
            var second = Ide("2022", "Enterprise");

            var folders = EditorLaunchProfiles.VisualStudioDirectories(programFiles, windows: true);

            Assert.Equal(3, folders.Length);
            Assert.Equal([first, second], folders[..2].Order());
            Assert.Equal(old, folders[2]);
            Assert.Empty(EditorLaunchProfiles.VisualStudioDirectories(programFiles, windows: false));
            Assert.Empty(EditorLaunchProfiles.VisualStudioDirectories(Path.Combine(programFiles, "missing"), windows: true));
        }
        finally
        {
            PrecastBrickFixture.DeleteTree(programFiles);
        }
    }

    /// <summary>Discovery searches the profile's names and folders, and reports one installation or none.</summary>
    [Fact]
    public void DiscoveryUsesTheProfilesNamesAndFolders()
    {
        var editor = Editor("zed");
        IEnumerable<string>? names = null;
        IEnumerable<string>? directories = null;
        editor.Locate = (searched, folders) =>
        {
            names = searched;
            directories = folders;
            return "/opt/zed/bin/zed";
        };

        var found = Assert.Single(editor.Discover());

        Assert.Equal(new EditorInstallation("zed", "Zed", "/opt/zed/bin/zed"), found);
        Assert.Equal(editor.Profile.Discovery.Names, names);
        Assert.Equal(editor.Profile.Discovery.Directories, directories);

        editor.Locate = static (_, _) => null;
        Assert.Empty(editor.Discover());
    }

    /// <summary>A browsed program is recognised by its file name, ignoring extension and case.</summary>
    [Theory]
    [InlineData("zed", "/home/u/.local/bin/zed", true)]
    [InlineData("zed", "/usr/bin/zeditor", true)]
    [InlineData("vscode", "/Tools/CODE.CMD", true)]
    [InlineData("rider", "/snap/bin/rider", true)]
    [InlineData("rider", "/usr/bin/zed", false)]
    public void RecognisesItsOwnProgram(string id, string path, bool expected) =>
        Assert.Equal(expected, Editor(id).Recognizes(path));

    /// <summary>The window values store as short names that parse back, and anything else parses as the default.</summary>
    [Fact]
    public void WindowValuesRoundTrip()
    {
        foreach (var window in Enum.GetValues<ExternalEditorWindow>())
        {
            Assert.Equal(window, ExternalEditorWindows.Parse(ExternalEditorWindows.ToValue(window)));
            Assert.Equal(window, ExternalEditorWindows.Parse(ExternalEditorWindows.ToValue(window).ToUpperInvariant()));
            Assert.False(string.IsNullOrWhiteSpace(ExternalEditorWindows.Label(window)));
        }

        Assert.Equal(ExternalEditorWindow.NewUnlessOpen, ExternalEditorWindows.Parse(null));
        Assert.Equal(ExternalEditorWindow.NewUnlessOpen, ExternalEditorWindows.Parse("nonsense"));
    }

    /// <summary>A choice option accepts only its listed values; other kinds accept any text.</summary>
    [Fact]
    public void ChoiceOptionsAcceptOnlyListedValues()
    {
        var choice = new ExternalEditorOption("k", "K", ExternalEditorOptionKind.Choice, "a", Choices: [new("a", "A"), new("b", "B")]);

        Assert.True(choice.Accepts("B"));
        Assert.False(choice.Accepts("c"));
        Assert.Equal("a", choice.ValueIn(new Dictionary<string, string> { ["k"] = "c" }));
        Assert.True(new ExternalEditorOption("k", "K", ExternalEditorOptionKind.Text).Accepts("anything"));
        Assert.False(new ExternalEditorOption("k", "K", ExternalEditorOptionKind.Choice).Accepts("a"));
    }

    /// <summary>The custom editor is folder-based until its toggle is on; then it receives the solution, and a template can still name either.</summary>
    [Fact]
    public void CustomEditorSolutionAwareToggle()
    {
        var custom = new CustomEditor();
        var option = Assert.Single(custom.Options);
        Assert.Equal(CustomEditor.SolutionAwareKey, option.Key);
        Assert.Equal(ExternalEditorOptionKind.Toggle, option.Kind);
        Assert.Equal("false", option.DefaultValue);

        static EditorInstallation Installation(string? template, string? aware) => new(CustomEditor.EditorId, "Custom", "/opt/ed",
            Arguments: template, Options: aware is null ? null : new Dictionary<string, string> { [CustomEditor.SolutionAwareKey] = aware });

        Assert.Equal([Project], custom.BuildCommand(Installation(null, null), Open()).Arguments);
        Assert.Equal([Project], custom.BuildCommand(Installation(null, "false"), Open()).Arguments);
        Assert.Equal([Solution], custom.BuildCommand(Installation(null, "true"), Open()).Arguments);
        Assert.Equal([Project], custom.BuildCommand(Installation(null, "true"), Open(solution: false)).Arguments);
        Assert.Equal([Script], custom.BuildCommand(Installation(null, "true"), OpenFile()).Arguments);
        Assert.Equal(["--open", Solution], custom.BuildCommand(Installation("--open {solution}", null), Open()).Arguments);
        Assert.Equal(["--open", Solution], custom.BuildCommand(Installation("--open {target}", "true"), Open()).Arguments);
        Assert.Equal(["--open", Project], custom.BuildCommand(Installation("--open {target}", null), Open()).Arguments);
    }

    /// <summary>The shipped editors are defaults, and one registered, attributed or unregistered replaces or restores them by id.</summary>
    [Fact]
    public void RegistryShipsProfilesThatBricksReplace()
    {
        var registry = new ExternalEditorRegistry(NullLogger.Instance);
        Assert.All(EditorLaunchProfiles.BuiltIn, profile => Assert.IsType<ProfileEditor>(registry.Find(profile.Id)));
        Assert.IsType<CustomEditor>(registry.Find(CustomEditor.EditorId));

        var replacement = new DummyEditor("zed");
        registry.Register(replacement);
        Assert.Same(replacement, registry.Find("zed"));
        Assert.Single(registry.Editors, static editor => editor.Id == "zed");

        Assert.True(registry.Unregister("zed"));
        Assert.IsType<ProfileEditor>(registry.Find("zed"));
        Assert.False(registry.Unregister("zed"));
        Assert.False(registry.Unregister("rider"));
        Assert.IsType<ProfileEditor>(registry.Find("rider"));
    }

    /// <summary>An editor a brick attributes replaces a default of the same id, and a default no brick covers stays.</summary>
    [Fact]
    public void AttributedEditorReplacesADefault()
    {
        var registry = new ExternalEditorRegistry(NullLogger.Instance,
            [new DummyEditor(AttributedDummyEditor.EditorId, "/built-in"), new DummyEditor("kept", "/kept")]);

        Assert.IsType<AttributedDummyEditor>(registry.Find(AttributedDummyEditor.EditorId));
        Assert.Single(registry.Editors, static editor => editor.Id == AttributedDummyEditor.EditorId);
        Assert.DoesNotContain(registry.DiscoverAll(), static found => found.Executable == "/built-in");
        Assert.Contains(registry.DiscoverAll(), static found => found.Executable == "/kept");
    }

    /// <summary>Registered and attributed editors are listed before the shipped defaults.</summary>
    [Fact]
    public void DefaultsAreListedLast()
    {
        var registry = new ExternalEditorRegistry(NullLogger.Instance);
        registry.Register(new DummyEditor("mine"));

        var ids = registry.Editors.Select(static editor => editor.Id).ToList();

        Assert.True(ids.IndexOf("mine") < ids.IndexOf(CustomEditor.EditorId));
        Assert.True(ids.IndexOf(AttributedDummyEditor.EditorId) < ids.IndexOf("zed"));
    }
}
