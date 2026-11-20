using System.CommandLine;

namespace Turian.Tests;

/// <summary>The <c>editor</c> commands read and write the same preference files Studio uses.</summary>
[Collection(SerialTests.Name)]
public sealed class ExternalEditorCliTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-editor-cli-").FullName;

    /// <inheritdoc/>
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    async Task<string> CreateProject() =>
        (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "My Game é")))!;

    string Editor()
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "Mi Editor é")).FullName;
        var executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "ed é.exe" : "ed é");
        File.WriteAllText(executable, string.Empty);
        return executable;
    }

    async Task<(int Exit, string Output)> Run(params string[] arguments)
    {
        var factory = typeof(global::Turian.Editor.CLI.Program)
            .GetMethod("EditorCommand", BindingFlags.Static | BindingFlags.NonPublic)!;
        var command = (Command)factory.Invoke(null, [new Argument<FileSystemInfo>("ProjectPath")])!;
        var writer = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(writer);
        try
        {
            var exit = await new RootCommand { command }.Parse(["editor", .. arguments])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
            return (exit, writer.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>A custom editor with spaces and Unicode in its path is stored personally and reported as ready.</summary>
    [Fact]
    public async Task SetStoresPersonallyAndGetReportsIt()
    {
        var project = await CreateProject();
        var executable = Editor();

        Assert.Equal(0, (await Run("set", "custom", project, "--path", executable, "--arguments", "--open \"{file}\"")).Exit);
        var (exit, output) = await Run("get", project, "--json");

        Assert.Equal(0, exit);
        var result = Parse(output);
        Assert.Equal("custom", result.GetProperty("editor").GetString());
        Assert.Equal(executable, result.GetProperty("executable").GetString());
        Assert.Equal("--open \"{file}\"", result.GetProperty("arguments").GetString());
        Assert.Equal("personal", result.GetProperty("scope").GetString());
        Assert.Equal("Ready", result.GetProperty("status").GetString());
    }

    /// <summary>A higher scope overrides a lower one, and resetting it lets the lower one show again.</summary>
    [Fact]
    public async Task HigherScopeWinsAndResetFallsThrough()
    {
        var project = await CreateProject();
        var executable = Editor();
        await Run("set", "custom", project, "--path", executable, "--scope", "user");
        await Run("set", "default", project, "--scope", "personal");

        Assert.Equal("default", Parse((await Run("get", project, "--json")).Output).GetProperty("editor").GetString());

        await Run("reset", project, "--scope", "personal");
        var user = Parse((await Run("get", project, "--json")).Output);
        Assert.Equal("custom", user.GetProperty("editor").GetString());
        Assert.Equal("user", user.GetProperty("scope").GetString());

        Assert.Equal(0, (await Run("reset", project, "--all")).Exit);
        Assert.Equal(JsonValueKind.Null, Parse((await Run("get", project, "--json")).Output).GetProperty("scope").ValueKind);
        Assert.Empty(new ExternalEditorPreference(project).Holding());
    }

    /// <summary>A value Studio's settings wrote is what the command line reports.</summary>
    [Fact]
    public async Task GetReportsWhatStudioWrote()
    {
        var project = await CreateProject();
        Assert.True(new ExternalEditorPreference(project).Write(SettingsScope.Workspace,
            new ExternalEditorChoice("custom", Editor(), "{file}")));

        var result = Parse((await Run("get", project, "--json")).Output);

        Assert.Equal("workspace", result.GetProperty("scope").GetString());
        Assert.Equal("Ready", result.GetProperty("status").GetString());
    }

    /// <summary>A bad editor, scope or option is refused and nothing is written.</summary>
    [Fact]
    public async Task InvalidRequestsWriteNothing()
    {
        var project = await CreateProject();
        var executable = Editor();

        Assert.Equal(1, (await Run("set", "no-such-editor", project, "--path", executable)).Exit);
        Assert.Equal(1, (await Run("set", "custom", project, "--path", executable, "--scope", "nowhere")).Exit);
        Assert.Equal(1, (await Run("set", "custom", project, "--path", executable, "--option", "reuse=true")).Exit);
        Assert.Equal(1, (await Run("set", "custom", project)).Exit);
        Assert.Empty(new ExternalEditorPreference(project).Holding());
    }

    /// <summary>The window option takes only values the editor offers, and an editor that cannot choose a window has none.</summary>
    [Fact]
    public async Task WindowOptionIsCheckedAgainstTheEditorsChoices()
    {
        var project = await CreateProject();
        var executable = Editor();

        Assert.Equal(1, (await Run("set", "zed", project, "--path", executable, "--option", "window=sideways")).Exit);
        Assert.Equal(1, (await Run("set", "rider", project, "--path", executable, "--option", "window=new")).Exit);
        Assert.Empty(new ExternalEditorPreference(project).Holding());

        Assert.Equal(0, (await Run("set", "zed", project, "--path", executable, "--option", "window=reuse")).Exit);

        var result = Parse((await Run("get", project, "--json")).Output);
        Assert.Equal("zed", result.GetProperty("editor").GetString());
        Assert.Equal("reuse", result.GetProperty("options").GetProperty("window").GetString());
    }

    /// <summary>The custom editor's solution-aware switch is stored like any option.</summary>
    [Fact]
    public async Task CustomSolutionAwareSwitchIsStored()
    {
        var project = await CreateProject();

        Assert.Equal(0, (await Run("set", "custom", project, "--path", Editor(), "--option", "solutionAware=true")).Exit);

        var result = Parse((await Run("get", project, "--json")).Output);
        Assert.Equal("true", result.GetProperty("options").GetProperty("solutionAware").GetString());
    }

    /// <summary>With no path, the editor's first discovered installation is stored.</summary>
    [Fact]
    public async Task SetWithoutAPathUsesTheDiscoveredInstallation()
    {
        var project = await CreateProject();

        Assert.Equal(0, (await Run("set", AttributedDummyEditor.EditorId, project)).Exit);

        var result = Parse((await Run("get", project, "--json")).Output);
        Assert.Equal(AttributedDummyEditor.EditorId, result.GetProperty("editor").GetString());
        Assert.Equal("/opt/attributed/editor", result.GetProperty("executable").GetString());
    }

    /// <summary>Arguments belong to the custom editor, and an option has to be one the editor offers.</summary>
    [Fact]
    public async Task ArgumentsAndOptionsAreCheckedAgainstTheEditor()
    {
        var project = await CreateProject();

        Assert.Equal(1, (await Run("set", AttributedDummyEditor.EditorId, project, "--arguments", "{file}")).Exit);
        Assert.Equal(1, (await Run("set", AttributedDummyEditor.EditorId, project, "--option", "reuse=true")).Exit);
        Assert.Equal(1, (await Run("set", AttributedDummyEditor.EditorId, project, "--option", "novalue")).Exit);
        Assert.Empty(new ExternalEditorPreference(project).Holding());
    }

    /// <summary>Plain output names the editor, program, scope and status; listing prints one line per installation.</summary>
    [Fact]
    public async Task PlainOutputNamesWhatApplies()
    {
        var project = await CreateProject();
        await Run("set", "custom", project, "--path", Editor());
        await Run("reset", project);

        var (_, listed) = await Run("list");
        var (_, shown) = await Run("get", project);

        Assert.Contains(AttributedDummyEditor.EditorId, listed, StringComparison.Ordinal);
        Assert.Contains("editor: default", shown, StringComparison.Ordinal);
        Assert.Contains("scope: none", shown, StringComparison.Ordinal);
        Assert.Contains("status: SystemDefault", shown, StringComparison.Ordinal);
    }

    /// <summary>A missing executable is reported by status and by the test command, which exits unsuccessfully.</summary>
    [Fact]
    public async Task MissingExecutableIsReportedAndFailsTheTest()
    {
        var project = await CreateProject();
        var missing = Path.Combine(root, "gone", "ed");
        await Run("set", "custom", project, "--path", missing);

        var result = Parse((await Run("get", project, "--json")).Output);
        Assert.Equal("ExecutableMissing", result.GetProperty("status").GetString());
        Assert.Equal(1, (await Run("test", project)).Exit);
    }

    static string Lines(string output) => output.ReplaceLineEndings("\n").TrimEnd();

    static string Encoded(string value) => JsonEncodedText.Encode(value).ToString();

    /// <summary>
    /// The contract of <c>editor get --json</c>: IDE bricks and scripts read this text, so every property, its order and
    /// its value kind is pinned. Changing any of it is a breaking change that needs a ruling.
    /// </summary>
    [Fact]
    public async Task GetJsonContractIsPinned()
    {
        var project = await CreateProject();
        var executable = Editor();
        await Run("set", "custom", project, "--path", executable, "--arguments", "--open {file}");
        var path = new ExternalEditorPreference(project).PathFor(SettingsScope.Personal)!;

        var (_, output) = await Run("get", project, "--json");

        Assert.Equal($$"""
            {
              "editor": "custom",
              "executable": "{{Encoded(executable)}}",
              "arguments": "--open {file}",
              "options": {},
              "scope": "personal",
              "path": "{{Encoded(path)}}",
              "status": "Ready",
              "project": "{{Encoded(project)}}"
            }
            """, Lines(output));
    }

    /// <summary>With nothing stored the same contract reports the system default, a null scope and path, and no options.</summary>
    [Fact]
    public async Task GetJsonContractForTheSystemDefaultIsPinned()
    {
        var project = await CreateProject();

        var (_, output) = await Run("get", project, "--json");

        Assert.Equal($$"""
            {
              "editor": "default",
              "executable": "",
              "arguments": "",
              "options": {},
              "scope": null,
              "path": null,
              "status": "SystemDefault",
              "project": "{{Encoded(project)}}"
            }
            """, Lines(output));
    }

    /// <summary>The contract of <c>editor list --json</c>: one object per installation with these four properties, in this order.</summary>
    [Fact]
    public async Task ListJsonContractIsPinned()
    {
        var (_, output) = await Run("list", "--json");

        var entry = Parse(output).EnumerateArray()
            .Single(static element => element.GetProperty("editor").GetString() == AttributedDummyEditor.EditorId);

        Assert.Equal(["editor", "name", "version", "executable"], entry.EnumerateObject().Select(static p => p.Name));
        Assert.Equal("/opt/attributed/editor", entry.GetProperty("executable").GetString());
    }

    /// <summary>Listing installations succeeds and prints JSON an automation can parse.</summary>
    [Fact]
    public async Task ListPrintsJson()
    {
        var (exit, output) = await Run("list", "--json");

        Assert.Equal(0, exit);
        Assert.Equal(JsonValueKind.Array, Parse(output).ValueKind);
    }
}
