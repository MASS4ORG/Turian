namespace Turian.Tests;

/// <summary>Launching reports failures as notifications, falls back to the system handler and hints once.</summary>
public sealed class ExternalEditorLauncherTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-launcher-").FullName;
    readonly ExternalEditorRegistry registry = new(NullLogger.Instance);
    readonly EditorNotifications notifications = new();
    readonly List<string> systemOpened = [];

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    ExternalEditorLauncher Create(ExternalEditorSelection? selection = null) =>
        new(registry, notifications, NullLogger.Instance)
        {
            Selection = selection,
            OpenWithSystem = path =>
            {
                systemOpened.Add(path);
                return Task.CompletedTask;
            },
        };

    string Executable()
    {
        var path = Path.Combine(root, "editor");
        File.WriteAllText(path, string.Empty);
        return path;
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

    /// <summary>Without an editor the file opens with the system default and a hint appears only the first time.</summary>
    [Fact]
    public async Task FallsBackToTheSystemHandlerWithAOneTimeHint()
    {
        var launcher = Create();
        var context = new EditorContext("/project", File: "/project/Assets/a.cs", Line: 3);

        Assert.True(await launcher.OpenAsync(context, TestContext.Current.CancellationToken));
        Assert.True(await launcher.OpenAsync(context, TestContext.Current.CancellationToken));

        Assert.Equal(["/project/Assets/a.cs", "/project/Assets/a.cs"], systemOpened);
        var hint = Assert.Single(Drain());
        Assert.False(hint.IsError);
        Assert.Equal(ExternalEditorLauncher.SettingsPath, hint.SettingsPath);
    }

    /// <summary>
    /// With no file the system handler gets the project folder, never the solution: whichever program is registered for
    /// the solution file could be an editor already showing another project.
    /// </summary>
    [Fact]
    public async Task FallbackOpensTheProjectFolderNeverTheSolution()
    {
        var launcher = Create();

        await launcher.OpenAsync(new EditorContext("/project", Solution: "/project/.Cache/Game.slnx"),
            TestContext.Current.CancellationToken);
        await launcher.OpenAsync(new EditorContext("/project"), TestContext.Current.CancellationToken);

        Assert.Equal(["/project", "/project"], systemOpened);
    }

    /// <summary>A system handler that cannot open the file is a failure notification, not an exception.</summary>
    [Fact]
    public async Task FailingFallbackIsReported()
    {
        var launcher = Create();
        launcher.OpenWithSystem = static _ => throw new IOException("no handler");

        Assert.False(await launcher.OpenAsync(new EditorContext("/project", File: "/project/a.cs"),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(Drain());
        Assert.True(failure.IsError);
        Assert.Equal(ExternalEditorLauncher.SettingsPath, failure.SettingsPath);
    }

    /// <summary>The chosen editor opens the context and nothing falls back or hints.</summary>
    [Fact]
    public async Task OpensWithTheChosenEditor()
    {
        var editor = new DummyEditor("chosen");
        registry.Register(editor);
        var launcher = Create(new ExternalEditorSelection("chosen", Executable()));
        var context = new EditorContext("/project", File: "/project/a.cs", Line: 2, Column: 5);

        Assert.True(await launcher.OpenAsync(context, TestContext.Current.CancellationToken));

        Assert.Equal([context], editor.Opened);
        Assert.Empty(systemOpened);
        Assert.Empty(Drain());
    }

    /// <summary>A stored executable that no longer exists is reported with the way to fix it.</summary>
    [Fact]
    public async Task MissingExecutableIsReported()
    {
        registry.Register(new DummyEditor("chosen"));
        var launcher = Create(new ExternalEditorSelection("chosen", Path.Combine(root, "gone")));

        Assert.False(await launcher.OpenAsync(new EditorContext("/project"), TestContext.Current.CancellationToken));

        var failure = Assert.Single(Drain());
        Assert.True(failure.IsError);
        Assert.Contains("gone", failure.Message, StringComparison.Ordinal);
        Assert.Equal(ExternalEditorLauncher.SettingsPath, failure.SettingsPath);
    }

    /// <summary>An editor that was removed with its brick is reported, and Studio stays usable.</summary>
    [Fact]
    public async Task UnknownEditorIsReported()
    {
        var launcher = Create(new ExternalEditorSelection("removed-brick", Executable()));

        Assert.False(await launcher.OpenAsync(new EditorContext("/project"), TestContext.Current.CancellationToken));

        Assert.Contains("removed-brick", Assert.Single(Drain()).Message, StringComparison.Ordinal);
    }

    /// <summary>An editor that fails to start becomes a failure notification carrying its message.</summary>
    [Fact]
    public async Task EditorFailureIsReported()
    {
        registry.Register(new DummyEditor("chosen") { Failure = new InvalidOperationException("Dummy chosen exited with code 2.") });
        var launcher = Create(new ExternalEditorSelection("chosen", Executable()));

        Assert.False(await launcher.OpenAsync(new EditorContext("/project"), TestContext.Current.CancellationToken));

        var failure = Assert.Single(Drain());
        Assert.True(failure.IsError);
        Assert.Equal("Dummy chosen exited with code 2.", failure.Message);
    }

    /// <summary>A program that cannot be started at all, which is not an invalid operation, is still a notification.</summary>
    [Theory]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task EditorThatCannotBeStartedIsReported(Type failureType)
    {
        var failure = (Exception)Activator.CreateInstance(failureType, "denied")!;
        registry.Register(new DummyEditor("chosen") { Failure = failure });
        var launcher = Create(new ExternalEditorSelection("chosen", Executable()));

        Assert.False(await launcher.OpenAsync(new EditorContext("/project"), TestContext.Current.CancellationToken));

        var notification = Assert.Single(Drain());
        Assert.True(notification.IsError);
        Assert.Contains("denied", notification.Message, StringComparison.Ordinal);
        Assert.Equal(ExternalEditorLauncher.SettingsPath, notification.SettingsPath);
    }
}
