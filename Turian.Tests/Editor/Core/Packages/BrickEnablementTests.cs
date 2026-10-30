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
        Directory.Delete(root, recursive: true);
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

    ResolvedPackage PrecastBrick(string name, List<PackageScope> scopes)
    {
        var id = "test." + name.ToLowerInvariant();
        var folder = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        var lib = Directory.CreateDirectory(Path.Combine(BrickAssemblies.PrecastDirectory(folder), "lib")).FullName;
        Compile(name, Path.Combine(lib, name + ".dll"));
        var manifest = new PackageManifest { Name = id, Version = SemanticVersion.Parse("0.1.0"), Scopes = scopes };
        return new ResolvedPackage(id, manifest, folder, PackageOrigin.Builtin, $"builtin:{id}", null, null, 1, false);
    }

    static void Compile(string name, string path)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(DataAsset).Assembly.Location)
            .Distinct()
            .Select(static reference => MetadataReference.CreateFromFile(reference));
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText($$"""
                [Turian.CreateAssetMenu("Tests/{{name}}")]
                public class {{name}}Data : Turian.Engine.Core.DataAsset { }
                """)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }
}
