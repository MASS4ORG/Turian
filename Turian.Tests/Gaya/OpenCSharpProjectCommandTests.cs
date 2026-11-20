namespace Turian.Tests;

/// <summary>Project → Open C# Project regenerates the solution, then opens it, and opens nothing when that fails.</summary>
public sealed class OpenCSharpProjectCommandTests : IDisposable
{
    const string commandId = "gaya.turian.openCSharpProject";

    readonly string root = Directory.CreateTempSubdirectory("turian-opencs-").FullName;
    readonly DummyEditor editor = new("chosen");
    readonly BackgroundTaskManager tasks = new();
    readonly EditorNotifications notifications = new();
    readonly SettingsService project = new();
    readonly StudioContributionsTests.Contributions contributions = StudioContributionsTests.Configure();
    readonly CodeOpener opener;
    readonly ServiceProvider provider;
    readonly CommandDescriptor command;

    /// <summary>Wires the command to a stub editor and a stubbed system handler.</summary>
    public OpenCSharpProjectCommandTests()
    {
        var program = Path.Combine(root, "editor");
        File.WriteAllText(program, string.Empty);
        var registry = new ExternalEditorRegistry(NullLogger.Instance);
        registry.Register(editor);
        var launcher = new ExternalEditorLauncher(registry, notifications, NullLogger.Instance)
        {
            Selection = new ExternalEditorSelection("chosen", program),
            OpenWithSystem = static _ => Task.CompletedTask,
        };
        opener = new CodeOpener(project, launcher, notifications, NullLogger.Instance);
        provider = new ServiceCollection()
            .AddSingleton(project)
            .AddSingleton(opener)
            .AddSingleton(new BackgroundTaskRunner(tasks, NullLogger.Instance))
            .BuildServiceProvider();
        command = Assert.Single(contributions.Commands, static item => item.Id == commandId);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        provider.Dispose();
        Directory.Delete(root, recursive: true);
    }

    AppSettings OpenProject() =>
        project.Set(new AppSettings { ProjectAbsoluteDir = Path.Combine(root, "My Project ü"), Title = "Game" });

    async Task<BackgroundTask> Finished()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (tasks.Snapshot().SingleOrDefault() is { Status: BackgroundTaskStatus.Completed or BackgroundTaskStatus.Failed } done)
            {
                return done;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The Open C# project task did not finish.");
    }

    /// <summary>The command sits in the Project menu beside Regenerate C# Solution, and needs an open project.</summary>
    [Fact]
    public void SitsNextToRegenerateAndNeedsAProject()
    {
        var item = Assert.Single(contributions.Menus, static menu => menu.CommandId == commandId);
        var regenerate = Assert.Single(contributions.Menus,
            static menu => menu.CommandId == "gaya.turian.regenerateCSharpSolution");

        Assert.Equal(MenuIds.Project, item.MenuId);
        Assert.Equal(regenerate.Group, item.Group);
        Assert.Equal(regenerate.Order + 1, item.Order);
        Assert.Equal("Project: Open C# Project", command.Title);
        Assert.False(command.CanExecute!(provider));

        OpenProject();

        Assert.True(command.CanExecute(provider));
    }

    /// <summary>Ctrl+Shift+O runs the command, and no other Studio shortcut uses the same keys.</summary>
    [Fact]
    public void HasItsOwnShortcut()
    {
        var binding = Assert.Single(contributions.Shortcuts, static item => item.CommandId == commandId);

        Assert.Equal(new KeyStroke(GKey.O, KeyModifiers.Ctrl | KeyModifiers.Shift), binding.Stroke);
        Assert.Single(contributions.Shortcuts, item => item.Stroke == binding.Stroke && item.Context == binding.Context);
    }

    /// <summary>The task regenerates, opens the solution the regeneration produced, and completes.</summary>
    [Fact]
    public async Task RegeneratesThenOpensTheSolution()
    {
        var settings = OpenProject();
        var solution = Path.Combine(settings.ProjectAbsoluteDir, ".Cache", "Game.slnx");
        var regenerated = 0;
        opener.Regenerate = (_, _) =>
        {
            regenerated++;
            return new GeneratedProjectFiles(solution, [], true);
        };

        command.Execute(provider);

        Assert.Equal(BackgroundTaskStatus.Completed, (await Finished()).Status);
        Assert.Equal(1, regenerated);
        Assert.Equal(solution, Assert.Single(editor.Opened).Solution);
    }

    /// <summary>A failed regeneration ends the task as failed, opens nothing and tells the user.</summary>
    [Fact]
    public async Task FailedRegenerationFailsTheTaskAndOpensNothing()
    {
        OpenProject();
        opener.Regenerate = static (_, _) => throw new IOException("disk full");

        command.Execute(provider);

        var done = await Finished();
        Assert.Equal(BackgroundTaskStatus.Failed, done.Status);
        Assert.Empty(editor.Opened);
        var failure = Assert.Single(Drain());
        Assert.True(failure.IsError);
        Assert.Contains("disk full", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>An editor that is missing after a good regeneration also ends the task as failed.</summary>
    [Fact]
    public async Task EditorThatCannotStartFailsTheTask()
    {
        var settings = OpenProject();
        opener.Regenerate = (_, _) => new GeneratedProjectFiles(Path.Combine(settings.ProjectAbsoluteDir, "g.slnx"), [], true);
        File.Delete(Path.Combine(root, "editor"));

        command.Execute(provider);

        Assert.Equal(BackgroundTaskStatus.Failed, (await Finished()).Status);
        Assert.Empty(editor.Opened);
        Assert.True(Assert.Single(Drain()).IsError);
    }

    List<EditorNotification> Drain()
    {
        var taken = new List<EditorNotification>();
        while (notifications.TryTake(out var notification))
        {
            taken.Add(notification);
        }

        return taken;
    }
}
