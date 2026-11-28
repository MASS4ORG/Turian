namespace Turian.Tests;

/// <summary>What a project brick contributes through its live plugin appears and disappears with it.</summary>
[Collection(SerialTests.Name)]
public sealed class LivePluginContributionTests : IDisposable
{
    const string Canvas = "test.fake.canvas";

    readonly string root = Directory.CreateTempSubdirectory("turian-live-brick-").FullName;

    /// <summary>Starts without an open project's bricks, which earlier project tests leave behind.</summary>
    public LivePluginContributionTests() => BrickAssemblies.ResetEnablement();

    /// <inheritdoc />
    public void Dispose()
    {
        BrickAssemblies.ResetEnablement();
        PrecastBrickFixture.DeleteTree(root);
    }

    sealed class Entries : IAssetCreationProvider
    {
        public Exception? Failure { get; init; }

        public IReadOnlyList<AssetCreationKind> Kinds => Failure is { } failure
            ? throw failure
            : [new AssetCreationKind("UI/Menu", "Menu.fake") { Creator = static _ => null }];
    }

    sealed class BlankPanel : IPanel, IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Render(Gui gui)
        {
        }

        public void Dispose() => Disposed = true;
    }

    static AssetCreationCatalog CreationCatalog(DocumentProject project, BuildManager build, AssetImporter importer) =>
        new(new AssetFileSystem(project.Settings, importer), project.Settings, build, NullLogger.Instance);

    /// <summary>
    /// Kinds, creation entries, inspector sources, document kinds and document panels come with the activation and go
    /// with the lifetime, clean tabs closing and returning across a recompile.
    /// </summary>
    [Fact]
    public void ContributionsComeAndGoWithTheAssembly()
    {
        using var project = new DocumentProject(liveKinds: true);
        using var build = new BuildManager(new AppSettings { ProjectAbsoluteDir = project.Root }, NullLogger.Instance);
        using var importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), project.Settings, build);
        importer.StopMonitoring();
        using var creation = CreationCatalog(project, build, importer);
        var inspectors = new InspectorSourceCatalog();
        var panels = new List<BlankPanel>();
        using var frame = new WorkbenchFrame();
        using var harness = new LivePluginHarness();
        Func<LivePluginContext, string, IDisposable> contribute = (context, _) =>
        {
            var registrations = new[]
            {
                project.Registry.Add(new FakeDocumentKind()),
                creation.Add(new Entries()),
                inspectors.Add(new FakeInspectorSource()),
            };
            context.DocumentKinds.Register(new DocumentKind(FakeDocumentKind.KindId, "Fake")
            {
                Placements = new Dictionary<string, PanelPlacement> { [Canvas] = PanelPlacement.Left },
            });
            context.Panels.Register(PanelDescriptor.ForDocuments(Canvas, "Canvas", PanelPlacement.Center,
                [FakeDocumentKind.KindId], (_, _) =>
                {
                    panels.Add(new BlankPanel());
                    return panels[^1];
                }));
            return new Withdrawal(() =>
            {
                context.Panels.Remove(Canvas);
                context.DocumentKinds.Remove(FakeDocumentKind.KindId);
                foreach (var registration in registrations.Reverse())
                {
                    registration.Dispose();
                }
            });
        };
        var services = new ServiceCollection().AddSingleton(contribute).BuildServiceProvider();
        var context = new LivePluginContext(services, frame.Panels, frame.Commands, frame.Menus)
        {
            DocumentKinds = frame.DocumentKinds,
        };
        IReadOnlyList<Assembly> assemblies = [];
        using var bridge = new LivePluginBridge(() => assemblies, static () => "project", context,
            NullLogger.Instance);
        using var documents = new DocumentWorkspaceBridge(project.Workspace, frame.Documents);
        var (path, _) = project.Source("menu.fake");
        Assert.DoesNotContain(creation.Kinds, static kind => kind.MenuPath == "UI/Menu");

        assemblies = [harness.Load("Ui")];
        bridge.Sync();
        var tab = project.Workspace.Open(path)!;
        frame.Draw();
        frame.Draw();

        Assert.NotNull(tab.Document);
        Assert.Contains(creation.Kinds, static kind => kind.MenuPath == "UI/Menu");
        Assert.NotNull(inspectors.SourceFor(new FakeSubject()));
        Assert.NotNull(frame.DocumentKinds.Find(FakeDocumentKind.KindId));
        Assert.True(frame.Workbench.Layout.Contains(Canvas));
        var panel = Assert.Single(panels);

        assemblies = [];
        bridge.Sync();

        Assert.True(panel.Disposed);
        frame.Draw();
        Assert.Empty(project.Workspace.Documents);
        Assert.Empty(frame.Documents.Documents);
        Assert.Null(project.Workspace.KindFor(path));
        Assert.DoesNotContain(creation.Kinds, static kind => kind.MenuPath == "UI/Menu");
        Assert.Null(inspectors.SourceFor(new FakeSubject()));
        Assert.Null(frame.DocumentKinds.Find(FakeDocumentKind.KindId));
        Assert.DoesNotContain(frame.Panels.All, static descriptor => descriptor.Id == Canvas);

        assemblies = [harness.Load("Ui")];
        bridge.Sync();
        frame.Draw();
        frame.Draw();

        Assert.NotNull(Assert.Single(project.Workspace.Documents).Document);
        Assert.True(frame.Workbench.Layout.Contains(Canvas));
        Assert.Equal(2, panels.Count);
    }

    /// <summary>A creation provider that throws costs only its own entries, and removal restores the menu.</summary>
    [Fact]
    public void AFailingCreationProviderCostsOnlyItsEntries()
    {
        using var project = new DocumentProject();
        using var build = new BuildManager(new AppSettings { ProjectAbsoluteDir = project.Root }, NullLogger.Instance);
        using var importer = new AssetImporter(NullLogger.Instance, new AssetDatabase(), project.Settings, build);
        importer.StopMonitoring();
        using var creation = CreationCatalog(project, build, importer);

        using var broken = creation.Add(new Entries { Failure = new NotSupportedException("broken") });
        var working = creation.Add(new Entries());

        Assert.Contains(creation.Kinds, static kind => kind.MenuPath == "UI/Menu");
        Assert.Contains(creation.Kinds, static kind => kind.MenuPath == "Folder");
        working.Dispose();
        Assert.DoesNotContain(creation.Kinds, static kind => kind.MenuPath == "UI/Menu");
    }

    /// <summary>
    /// Enabling a prebuilt editor brick activates its plugin and lists its settings page; disabling it releases both,
    /// and the page's saved value is restored when the brick returns.
    /// </summary>
    [Fact]
    public void AnEditorBrickBringsItsPluginAndSettingsPage()
    {
        var name = "LiveBrick" + Guid.NewGuid().ToString("N");
        var brick = EditorBrick(name, PluginAssemblies.Delegating(name) + $$"""

            [Gaya.EditorSetting("Bricks/{{name}}", Id = "{{name}}")]
            public sealed class {{name}}Settings
            {
                public int Columns { get; set; } = 3;
            }
            """);
        Assert.True(BrickAssemblies.Load([brick], NullLogger.Instance));
        using var build = new BuildManager(new BuildAppSettings { ProjectAbsoluteDir = root }, NullLogger.Instance);
        var assemblies = new EditorAssemblySet(build);
        using var harness = new LivePluginHarness();
        using var bridge = new LivePluginBridge(() => assemblies.Current, static () => "project", harness.Context,
            NullLogger.Instance);
        var catalog = new UserSettingsCatalog(build, NullLogger.Instance, assemblies);
        var settings = new EditorSettings(NullLogger.Instance, Path.Combine(root, "user.json"));
        using var pages = new UserSettingsBridge(catalog, settings, NullLogger.Instance);

        BrickAssemblies.Enable([brick]);
        bridge.Sync();
        pages.Sync();

        Assert.Equal([$"on {name}"], harness.Trace);
        var page = Assert.Single(settings.Pages, registered => registered.Id == name);
        Assert.True(settings.Write(name, "Columns", SettingsScope.User, 8).Succeeded);
        settings.Save();

        BrickAssemblies.Enable([]);
        bridge.Sync();
        pages.Sync();

        Assert.Equal([$"on {name}", $"off {name}"], harness.Trace);
        Assert.DoesNotContain(settings.Pages, registered => registered.Id == name);

        BrickAssemblies.Enable([brick]);
        pages.Sync();

        var restored = Assert.Single(settings.Pages, registered => registered.Id == name);
        Assert.NotSame(page.Target, restored.Target);
        Assert.Equal(8, restored.Target.GetType().GetProperty("Columns")!.GetValue(restored.Target));
    }

    ResolvedPackage EditorBrick(string name, string source)
    {
        var id = "test." + name.ToLowerInvariant();
        var folder = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        var editor = Path.Combine(BrickAssemblies.PrecastDirectory(folder), "editor");
        Directory.CreateDirectory(editor);
        PluginAssemblies.Emit(Path.Combine(editor, name + ".dll"), name, source);
        var manifest = new PackageManifest
        {
            Name = id,
            Version = SemanticVersion.Parse("0.1.0"),
            Scopes = [PackageScope.Project],
        };
        return new ResolvedPackage(id, manifest, folder, PackageOrigin.Builtin, $"builtin:{id}", null, null, 1, false);
    }
}
