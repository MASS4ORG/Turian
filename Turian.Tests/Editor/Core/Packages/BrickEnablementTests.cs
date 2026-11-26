namespace Turian.Tests;

/// <summary>Editor discovery follows the open project's enabled bricks, although brick assemblies never unload.</summary>
[Collection(SerialTests.Name)]
public sealed class BrickEnablementTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-brick-enable-").FullName;

    /// <summary>Starts without an open project's bricks, which earlier project tests leave behind.</summary>
    public BrickEnablementTests() => BrickAssemblies.ResetEnablement();

    /// <inheritdoc/>
    public void Dispose()
    {
        BrickAssemblies.ResetEnablement();
        PrecastBrickFixture.DeleteTree(root);
    }

    /// <summary>Disabling a precast game brick removes its Create entries and types from discovery.</summary>
    [Fact]
    public void DisabledBrickLeavesDiscovery()
    {
        var name = "Enable" + Guid.NewGuid().ToString("N");
        var brick = PrecastBrick(name, [PackageScope.Project]);
        Assert.True(BrickAssemblies.Load([brick], NullLogger.Instance));
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(loaded => loaded.GetName().Name == name);

        var settings = new SettingsService();
        var app = new AppSettings { ProjectAbsoluteDir = root };
        settings.Set(app);
        var log = Substitute.For<ILogger>();
        using var build = new BuildManager(app, log);
        using var importer = new AssetImporter(log, new AssetDatabase(), settings, build);
        importer.StopMonitoring();
        using var catalog = new AssetCreationCatalog(new AssetFileSystem(settings, importer), settings, build, log);

        BrickAssemblies.Enable([brick]);
        Assert.Contains(assembly, build.LoadedAssemblies);
        Assert.Contains(catalog.Kinds, kind => kind.PayloadType?.Assembly == assembly);

        BrickAssemblies.Enable([]);
        Assert.DoesNotContain(assembly, build.LoadedAssemblies);
        Assert.DoesNotContain(catalog.Kinds, kind => kind.PayloadType?.Assembly == assembly);

        BrickAssemblies.Enable([brick]);
        Assert.Contains(catalog.Kinds, kind => kind.PayloadType?.Assembly == assembly);
    }

    /// <summary>A disabled brick's asset importer stops claiming files, although its assembly stays loaded.</summary>
    [Fact]
    public void DisabledBrickImporterStopsClaimingFiles()
    {
        var name = "Importer" + Guid.NewGuid().ToString("N");
        var extension = $".{name[^6..].ToLowerInvariant()}";
        var brick = PrecastBrick(name, [PackageScope.Project], $$"""
            public class {{name}}Importer : Turian.Editor.Core.IAssetImporter
            {
                public bool IsValid(string filePath) => filePath.EndsWith("{{extension}}");
                public Turian.Engine.Core.Asset CreateAsset(string filePath) => new Turian.Engine.Core.Asset();
            }
            """);
        Assert.True(BrickAssemblies.Load([brick], NullLogger.Instance));

        var settings = new SettingsService();
        var app = new AppSettings { ProjectAbsoluteDir = root };
        settings.Set(app);
        var log = Substitute.For<ILogger>();
        using var build = new BuildManager(app, log);
        using var importer = new AssetImporter(log, new AssetDatabase(), settings, build);
        importer.StopMonitoring();
        var file = Path.Combine(root, "model" + extension);

        BrickAssemblies.Enable([brick]);
        importer.RefreshImporters();
        Assert.Equal($"{name}Importer", importer.ImporterFor(file)?.GetType().Name);

        BrickAssemblies.Enable([]);
        importer.RefreshImporters();
        Assert.NotEqual($"{name}Importer", importer.ImporterFor(file)?.GetType().Name);
    }

    /// <summary>Studio-only bricks stay discoverable; a game brick loaded by the studio follows the project.</summary>
    [Fact]
    public void OnlyGameBricksRetire()
    {
        var studioOnly = "StudioOnly" + Guid.NewGuid().ToString("N");
        var game = "StudioGame" + Guid.NewGuid().ToString("N");
        var studioBrick = PrecastBrick(studioOnly, [PackageScope.Studio]);
        var gameBrick = PrecastBrick(game, [PackageScope.Project, PackageScope.Studio]);
        Assert.True(BrickAssemblies.Load([studioBrick, gameBrick], NullLogger.Instance));
        var loaded = AppDomain.CurrentDomain.GetAssemblies();

        BrickAssemblies.RecordGameBricks([studioBrick, gameBrick]);
        Assert.False(BrickAssemblies.IsRetired(loaded.Single(assembly => assembly.GetName().Name == game)));

        BrickAssemblies.Enable([]);
        Assert.False(BrickAssemblies.IsRetired(loaded.Single(assembly => assembly.GetName().Name == studioOnly)));
        Assert.True(BrickAssemblies.IsRetired(loaded.Single(assembly => assembly.GetName().Name == game)));
        Assert.False(BrickAssemblies.IsRetired(typeof(DataAsset).Assembly));
    }

    /// <summary>Only enabled project editor assemblies and studio-only editor assemblies contribute panels.</summary>
    [Fact]
    public void EditorAssemblyDiscoveryFollowsScopeAndEnablement()
    {
        var studio = PrecastBrick("EditorStudio" + Guid.NewGuid().ToString("N"), [PackageScope.Studio], editor: true);
        var project = PrecastBrick("EditorProject" + Guid.NewGuid().ToString("N"), [PackageScope.Project], editor: true);
        var runtime = PrecastBrick("RuntimeProject" + Guid.NewGuid().ToString("N"), [PackageScope.Project]);
        BrickAssemblies.RecordGameBricks([studio, project, runtime]);
        Assert.True(BrickAssemblies.Load([studio, project, runtime], NullLogger.Instance));
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var studioAssembly = assemblies.Single(a => a.GetName().Name == Path.GetFileNameWithoutExtension(
            Assert.Single(BrickAssemblies.EditorAssemblies(studio))));
        var projectAssembly = assemblies.Single(a => a.GetName().Name == Path.GetFileNameWithoutExtension(
            Assert.Single(BrickAssemblies.EditorAssemblies(project))));
        var runtimeAssembly = assemblies.Single(a => a.GetName().Name == Path.GetFileNameWithoutExtension(
            Assert.Single(BrickAssemblies.RuntimeAssemblies(runtime))));

        Assert.True(BrickAssemblies.IsEditorAssembly(studioAssembly));
        Assert.False(BrickAssemblies.IsEditorAssembly(projectAssembly));
        BrickAssemblies.Enable([project, runtime]);
        Assert.True(BrickAssemblies.IsEditorAssembly(projectAssembly));
        Assert.False(BrickAssemblies.IsEditorAssembly(runtimeAssembly));
        BrickAssemblies.Enable([]);
        Assert.False(BrickAssemblies.IsEditorAssembly(projectAssembly));
        Assert.True(BrickAssemblies.IsEditorAssembly(studioAssembly));
    }

    /// <summary>Precast editor panels appear without user code and disappear when their brick is disabled.</summary>
    [Fact]
    public void CatalogDiscoversEnabledEditorBrickWithoutUserAssembly()
    {
        var name = "Panel" + Guid.NewGuid().ToString("N");
        var source = $$"""
            [{{typeof(PanelAttribute).FullName}}("Fixture", "Tools/{{name}}")]
            public class {{name}} { }
            """;
        var brick = PrecastBrick(name, [PackageScope.Project], source, editor: true);
        Assert.True(BrickAssemblies.Load([brick], NullLogger.Instance));
        BrickAssemblies.Enable([brick]);
        using var build = new BuildManager(new BuildAppSettings { ProjectAbsoluteDir = root }, NullLogger.Instance);
        using var catalog = new UserPanelCatalog(build, NullLogger.Instance);

        Assert.Null(build.ActiveUserAssembly);
        var page = Assert.Single(catalog.Pages, page => page.Path == $"Tools/{name}");
        catalog.EnsureRefreshed();
        Assert.Same(page, Assert.Single(catalog.Pages, item => item.Path == $"Tools/{name}"));
        BrickAssemblies.Enable([]);
        catalog.EnsureRefreshed();
        Assert.DoesNotContain(catalog.Pages, item => item.Path == $"Tools/{name}");
    }

    ResolvedPackage PrecastBrick(string name, List<PackageScope> scopes, string? source = null, bool editor = false)
    {
        var id = "test." + name.ToLowerInvariant();
        var folder = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        var lib = Directory.CreateDirectory(Path.Combine(BrickAssemblies.PrecastDirectory(folder), editor ? "editor" : "lib")).FullName;
        Compile(name, Path.Combine(lib, name + ".dll"), source);
        var manifest = new PackageManifest { Name = id, Version = SemanticVersion.Parse("0.1.0"), Scopes = scopes };
        return new ResolvedPackage(id, manifest, folder, PackageOrigin.Builtin, $"builtin:{id}", null, null, 1, false);
    }

    static void Compile(string name, string path, string? source = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(DataAsset).Assembly.Location)
            .Append(typeof(AssetImporter).Assembly.Location)
            .Append(typeof(PanelAttribute).Assembly.Location)
            .Distinct()
            .Select(static reference => MetadataReference.CreateFromFile(reference));
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(source ?? $$"""
                [Turian.CreateAssetMenu("Tests/{{name}}")]
                public class {{name}}Data : Turian.Engine.Core.DataAsset { }
                """)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }
}
