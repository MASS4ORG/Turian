namespace Turian.Tests;

/// <summary>Opening code targets the generated solution when there is one, and never a stale one after a failed regeneration.</summary>
public sealed class CodeOpenerTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-code-").FullName;
    readonly SettingsService project = new();
    readonly ExternalEditorRegistry registry = new(NullLogger.Instance);
    readonly EditorNotifications notifications = new();
    readonly DummyEditor editor = new("chosen");
    readonly List<string> systemOpened = [];
    readonly CodeOpener opener;

    /// <summary>Creates an opener over a stub editor and a stubbed system handler.</summary>
    public CodeOpenerTests()
    {
        var editorPath = Path.Combine(root, "editor");
        File.WriteAllText(editorPath, string.Empty);
        registry.Register(editor);
        var launcher = new ExternalEditorLauncher(registry, notifications, NullLogger.Instance)
        {
            Selection = new ExternalEditorSelection("chosen", editorPath),
            OpenWithSystem = path =>
            {
                systemOpened.Add(path);
                return Task.CompletedTask;
            },
        };
        opener = new CodeOpener(project, launcher, notifications, NullLogger.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    AppSettings OpenFolderProject()
    {
        var directory = Path.Combine(root, "My Project ü");
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        return project.Set(new AppSettings { ProjectAbsoluteDir = directory, Title = "Game" });
    }

    async Task<AppSettings> OpenRealProject()
    {
        var created = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "My Game é")))!;
        var settings = SettingsService.Load(created)!;
        ProjectSettingsLoader.LoadFromSources(new BuildAppSettings().Load(settings));
        return project.Set(settings);
    }

    string SolutionPath(AppSettings settings) =>
        ProjectRegeneration.SolutionPath((IBuildAppSettings)new BuildAppSettings().Load(settings));

    List<EditorNotification> Drain()
    {
        var taken = new List<EditorNotification>();
        while (notifications.TryTake(out var notification))
        {
            taken.Add(notification);
        }

        return taken;
    }

    /// <summary>Without an open project there is nothing to open and no editor is started.</summary>
    [Fact]
    public async Task NothingOpensWithoutAProject()
    {
        Assert.Null(opener.ContextFor());
        Assert.False(await opener.OpenAsync());
        Assert.False(await opener.OpenProjectAsync(TestContext.Current.CancellationToken));
        Assert.Empty(editor.Opened);
        Assert.Empty(Drain());
    }

    /// <summary>Before any solution exists the editor gets the project folder, and the file's place within it.</summary>
    [Fact]
    public async Task OpensTheProjectFolderUntilASolutionExists()
    {
        var settings = OpenFolderProject();
        var file = Path.Combine(settings.AssetsAbsoluteDir, "Player.cs");

        Assert.True(await opener.OpenAsync(file, 4, 9));

        var context = Assert.Single(editor.Opened);
        Assert.Equal(settings.ProjectAbsoluteDir, context.ProjectDirectory);
        Assert.Null(context.Solution);
        Assert.Equal(file, context.File);
        Assert.Equal((4, 9), (context.Line, context.Column));
    }

    /// <summary>Once the project's solution is generated, the editor opens that so references resolve.</summary>
    [Fact]
    public async Task OpensTheGeneratedSolutionWhenItExists()
    {
        var settings = OpenFolderProject();
        var solution = SolutionPath(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(solution)!);
        File.WriteAllText(solution, "<Solution />");

        Assert.True(await opener.OpenAsync());

        var context = Assert.Single(editor.Opened);
        Assert.Equal(solution, context.Solution);
        Assert.Null(context.File);
    }

    /// <summary>The editor opens the solution the regeneration returned, after the regeneration ran.</summary>
    [Fact]
    public async Task OpenProjectRegeneratesThenOpensTheSolution()
    {
        var settings = await OpenRealProject();

        Assert.True(await opener.OpenProjectAsync(TestContext.Current.CancellationToken));

        var solution = SolutionPath(settings);
        Assert.True(File.Exists(solution));
        var context = Assert.Single(editor.Opened);
        Assert.Equal(solution, context.Solution);
        Assert.Equal(settings.ProjectAbsoluteDir, context.ProjectDirectory);
        Assert.Empty(Drain());
    }

    /// <summary>
    /// A regeneration that fails leaves the stale solution on disk and must open nothing: no editor, no system
    /// fallback, a failure notification, and not a success result.
    /// </summary>
    [Fact]
    public async Task FailedRegenerationOpensNothingAndSaysSo()
    {
        var settings = OpenFolderProject();
        var stale = SolutionPath(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "<Solution />");
        opener.Regenerate = static (_, _) => throw new IOException("disk full");

        Assert.False(await opener.OpenProjectAsync(TestContext.Current.CancellationToken));

        Assert.Empty(editor.Opened);
        Assert.Empty(systemOpened);
        var failure = Assert.Single(Drain());
        Assert.True(failure.IsError);
        Assert.Contains("disk full", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not opened", failure.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(stale));
    }

    /// <summary>The same guarantee with the real regeneration failing on a real project that already has a solution.</summary>
    [Fact]
    public async Task RealRegenerationFailureOpensNothing()
    {
        var settings = await OpenRealProject();
        var first = ProjectRegeneration.Regenerate(settings, NullLogger.Instance);
        var project0 = Assert.Single(first.Projects);
        File.Delete(project0);
        Directory.CreateDirectory(project0);

        Assert.False(await opener.OpenProjectAsync(TestContext.Current.CancellationToken));

        Assert.True(File.Exists(first.Solution));
        Assert.Empty(editor.Opened);
        Assert.Empty(systemOpened);
        Assert.True(Assert.Single(Drain()).IsError);
    }

    /// <summary>A cancelled regeneration is cancellation, not a failure to report: it propagates and opens nothing.</summary>
    [Fact]
    public async Task CancelledRegenerationPropagatesAndOpensNothing()
    {
        OpenFolderProject();
        opener.Regenerate = static (_, _) => throw new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => opener.OpenProjectAsync(TestContext.Current.CancellationToken));

        Assert.Empty(editor.Opened);
        Assert.Empty(Drain());
    }
}
