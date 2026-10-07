namespace Turian.Tests;

/// <summary>Checks project presentation and deferred opening from menu and recent-project actions.</summary>
[Collection(SerialTests.Name)]
public sealed class ProjectChromeTests
{
    sealed class Harness : IDisposable
    {
        readonly SKSurface surface = SKSurface.Create(new SKImageInfo(900, 600));
        readonly Font font = Font.FromFamilyName("sans-serif", 14);

        /// <summary>Creates isolated editor services and records registered commands.</summary>
        public Harness()
        {
            var registrations = new ServiceCollection();
            registrations.AddSingleton<ILogger>(NullLogger.Instance);
            registrations.AddSingleton(Substitute.For<IShellHost>());
            registrations.AddSingleton(Commands);
            registrations.AddSingleton(Substitute.For<IPanelAccessor>());
            registrations.AddSingleton(Substitute.For<IShortcutService>());
            registrations.AddSingleton(Substitute.For<IFocusTracker>());
            registrations.AddSingleton(EditorSettings);
            Context.Services.Returns(registrations);
            Context.Logger.Returns(NullLogger.Instance);
            new GayaPlugin().Configure(Context);
            Services = registrations.BuildServiceProvider();
            Gui.Input = Input;
            Input.MousePosition.Returns(new Vector2(-1));
            Input.PrevMousePosition.Returns(new Vector2(-1));
        }

        /// <summary>Registration context retaining the main menu's commands.</summary>
        public IPluginContext Context { get; } = Substitute.For<IPluginContext>();
        /// <summary>Commands invoked by the project switcher.</summary>
        public ICommandDispatcher Commands { get; } = Substitute.For<ICommandDispatcher>();
        /// <summary>Isolated preferences.</summary>
        public IEditorSettings EditorSettings { get; } = Substitute.For<IEditorSettings>();
        /// <summary>Live editor service provider.</summary>
        public ServiceProvider Services { get; }
        /// <summary>Input supplied to both GUI passes.</summary>
        public IInputHandler Input { get; } = Substitute.For<IInputHandler>();
        /// <summary>Headless GUI containing the recent-project menu.</summary>
        public Gui Gui { get; } = new();

        /// <summary>Draws both passes with the supplied chrome item.</summary>
        public void Draw(IChromeItem item) => InspectorFormsRenderingTests.Frame(Gui, surface, font, item.Render);

        /// <summary>Clicks a menu node, then advances a frame with no pressed buttons.</summary>
        public void Click(IChromeItem item, string id)
        {
            var node = GayaChromeTests.Descendants(Gui.RootNode!).Single(node => node.Id == id);
            Input.MousePosition.Returns(node.Rect.Position + new Vector2(8, node.Rect.H / 2));
            Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            Draw(item);
            Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            Draw(item);
        }

        /// <summary>Releases the editor services and drawing surface.</summary>
        public void Dispose()
        {
            Services.Dispose();
            surface.Dispose();
        }
    }

    /// <summary>The visible project control fits its title, uses menu rows, and queues selected projects.</summary>
    [Fact]
    public void RecentProjectsFitNamesAndQueueOpening()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-project-menu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        try
        {
            using var harness = new Harness();
            var settings = harness.Services.GetRequiredService<SettingsService>();
            var project = new AppSettings { ProjectAbsoluteDir = root, Title = "Directory fallback" };
            settings.Set(project);
            var recent = new RecentProjectsSettings();
            recent.Add(root);
            using var chrome = new ProjectSwitcherChrome(recent, settings,
                harness.Services.GetRequiredService<ProjectSession>(), harness.Commands, harness.EditorSettings,
                harness.Services.GetRequiredService<UnsavedChangesGuard>());
            harness.Draw(chrome);
            var control = GayaChromeTests.Descendants(harness.Gui.RootNode!).Single(node => node.Id == "project-switcher");
            var initialWidth = control.Rect.W;
            project.Get<PlayerSettings>().ProductName = "A much longer configured project title";
            harness.Draw(chrome);
            control = GayaChromeTests.Descendants(harness.Gui.RootNode!).Single(node => node.Id == "project-switcher");
            Assert.True(control.Rect.W > initialWidth);
            harness.Click(chrome, "project-switcher");
            var row = GayaChromeTests.Descendants(harness.Gui.RootNode!).Single(node => node.Id == $"project-switcher/{root}/open");
            Assert.True(row.Rect.W > 100);
            harness.Click(chrome, $"project-switcher/{root}/open");
            Assert.False(harness.Services.GetRequiredService<ProjectSession>().IsOpening);

            settings.Set(new AppSettings { ProjectAbsoluteDir = "/another/project", Title = "Other" });
            harness.Draw(chrome);
            harness.Click(chrome, "project-switcher");
            harness.Click(chrome, $"project-switcher/{root}/open");
            Assert.True(harness.Services.GetRequiredService<ProjectSession>().IsOpening);
            Assert.Equal("Other", settings.Settings!.Title);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Menu rows dispatch Open Project and remove entries without button-style controls.</summary>
    [Fact]
    public void RecentMenuOpensPickerAndRemovesEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-project-menu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        try
        {
            using var harness = new Harness();
            var recent = new RecentProjectsSettings();
            recent.Add(root);
            using var chrome = new ProjectSwitcherChrome(recent, harness.Services.GetRequiredService<SettingsService>(),
                harness.Services.GetRequiredService<ProjectSession>(), harness.Commands, harness.EditorSettings,
                harness.Services.GetRequiredService<UnsavedChangesGuard>());
            harness.Draw(chrome);
            harness.Click(chrome, "project-switcher");
            harness.Click(chrome, "project-switcher/open");
            harness.Commands.Received(1).Execute("gaya.turian.openProject");
            harness.Click(chrome, "project-switcher");
            harness.Click(chrome, $"project-switcher/{root}/remove");
            Assert.Empty(recent.Paths);
            harness.EditorSettings.Received(1).NotifyChanged("gaya.turian.recentProjects");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The main menu's folder selection queues work instead of importing during a GUI callback.</summary>
    [Fact]
    public void MainMenuOpenProjectDefersWork()
    {
        using var harness = new Harness();
        var command = harness.Context.Commands.ReceivedCalls()
            .Select(call => call.GetArguments().FirstOrDefault()).OfType<CommandDescriptor>()
            .Single(command => command.Id == "gaya.turian.openProject");
        command.Execute(harness.Services);
        var dialog = harness.Services.GetRequiredService<FileDialogChrome>();
        var state = (FileDialogState)typeof(FileDialogChrome).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!;
        state.Request!.OnComplete!("/project/selected-from-menu");
        var session = harness.Services.GetRequiredService<ProjectSession>();
        Assert.True(session.IsOpening);
        Assert.Null(session.Settings);
    }

    /// <summary>Product names take priority over project titles and directory fallbacks.</summary>
    [Theory]
    [InlineData("Game Title", "Folder title", "Game Title")]
    [InlineData(" ", "Folder title", "Folder title")]
    [InlineData(null, null, "directory")]
    public void ProjectNameResolvesConfiguredTitle(string? product, string? title, string expected)
    {
        var project = new AppSettings { ProjectAbsoluteDir = "/projects/directory/", Title = title };
        project.Get<PlayerSettings>().ProductName = product;
        Assert.Equal(expected, ProjectPresentation.Name(project));
        Assert.Equal("missing", ProjectPresentation.Name("/does-not-exist/missing/"));
        Assert.Throws<ArgumentNullException>(() => ProjectPresentation.Name((IAppSettings)null!));
    }
}
