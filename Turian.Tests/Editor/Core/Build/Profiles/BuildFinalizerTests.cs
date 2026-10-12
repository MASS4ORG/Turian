namespace Turian.Tests;

/// <summary>The desktop platform's finalize step: strips artifacts, names the executable, and places settings and content.</summary>
public sealed class BuildFinalizerTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-finalizer-").FullName;

    /// <summary>Creates the project's Assets and Cache folders.</summary>
    public BuildFinalizerTests()
    {
        Directory.CreateDirectory(Assets);
        Directory.CreateDirectory(Path.Combine(project, ".Cache"));
    }

    string Assets => Path.Combine(project, "Assets");

    BuildAppSettings Settings => new() { Title = "Game", ProjectAbsoluteDir = project };

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);

    BuildPlatformContext Context(string output, string projectFile,
        BuildProfile? profile = null) =>
        new(Settings, NullLogger.Instance, projectFile, output, "Release", profile);

    /// <summary>Finalizing strips PDB, XML and deps files, keeps the assembly, and renames the executable.</summary>
    [Fact]
    public async Task FinalizeStripsArtifactsAndNamesTheExecutable()
    {
        var output = Path.Combine(project, "Output");
        Directory.CreateDirectory(output);
        foreach (var file in new[] { "Game.pdb", "Game.xml", "Game.deps.json", "Game.dll", "project" })
            File.WriteAllText(Path.Combine(output, file), string.Empty);

        var artifact = await new DesktopBuildPlatform().FinalizeAsync(Context(output, "project.csproj"),
            CancellationToken.None);

        Assert.Equal(output, artifact.OutputDirectory);
        Assert.False(File.Exists(Path.Combine(output, "Game.pdb")));
        Assert.False(File.Exists(Path.Combine(output, "Game.xml")));
        Assert.True(File.Exists(Path.Combine(output, "Game.dll")));
        Assert.True(File.Exists(Path.Combine(output, "Game")));
    }

    /// <summary>A published Windows executable is renamed to the title keeping its extension.</summary>
    [Fact]
    public void RenameExecutableUsesTheTitleAndExtension()
    {
        var output = Path.Combine(project, "Output");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "project.exe"), string.Empty);

        BuildFinalizer.RenameExecutable(Context(output, "project.csproj",
            new BuildProfile { RuntimeIdentifier = "win-x64" }), ".exe");

        Assert.False(File.Exists(Path.Combine(output, "project.exe")));
        Assert.True(File.Exists(Path.Combine(output, "Game.exe")));
    }

    /// <summary>The settings index and icon the game reads are copied beside the executable.</summary>
    [Fact]
    public void CopyProjectSettingsCopiesTheIndexAndIcon()
    {
        var output = Path.Combine(project, "Output");
        var cache = Path.Combine(project, ".Cache");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(cache, ProjectSettingsLoader.IndexFileName), "{}");
        File.WriteAllText(Path.Combine(cache, ProjectIcon.PngFileName), "png");

        BuildFinalizer.CopyProjectSettings(Context(output, "project.csproj"));

        Assert.True(File.Exists(Path.Combine(output, ProjectSettingsLoader.IndexFileName)));
        Assert.True(File.Exists(Path.Combine(output, ProjectIcon.PngFileName)));
    }

    /// <summary>Without a settings index the copy only warns, so the rest of the finalize step still runs.</summary>
    [Fact]
    public void CopyProjectSettingsToleratesAMissingIndex()
    {
        var output = Path.Combine(project, "Output");
        Directory.CreateDirectory(output);

        BuildFinalizer.CopyProjectSettings(Context(output, "project.csproj"));

        Assert.False(File.Exists(Path.Combine(output, ProjectSettingsLoader.IndexFileName)));
    }

    /// <summary>The component manifest the game registers user components through is written beside the executable.</summary>
    [Fact]
    public void WriteTypeManifestWritesTheManifest()
    {
        var output = Path.Combine(project, "Output");
        Directory.CreateDirectory(output);

        BuildFinalizer.WriteTypeManifest(Context(output, "project.csproj"));

        Assert.True(File.Exists(Path.Combine(output, UserCodeTypeManifest.FileName)));
    }

    /// <summary>Without an asset catalog the packaging step only warns and stops.</summary>
    [Fact]
    public void PackageAssetsSkipsWithoutACatalog()
    {
        var output = Path.Combine(project, "Output");
        Directory.CreateDirectory(output);

        BuildFinalizer.PackageAssets(Context(output, "project.csproj"));

        Assert.False(File.Exists(Path.Combine(output, "Game.oap")));
    }

    /// <summary>Export packs the artifact baked for the platform's texture target instead of the asset's primary one.</summary>
    [Fact]
    public void PackageAssetsPacksThePlatformsTextureTarget()
    {
        var output = Path.Combine(project, "Output");
        Directory.CreateDirectory(output);
        var id = Guid.NewGuid();
        var cache = Path.Combine(".Cache", "Assets", "by-guid", id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(project, cache));
        File.WriteAllBytes(Path.Combine(project, cache, "primary.other.ktx2"), [1]);
        File.WriteAllBytes(Path.Combine(project, cache, "primary.pc.ktx2"), [2]);
        var record = new AssetRecord
        {
            AssetId = id,
            AssetTypeName = "Turian.Engine.Core.TextureAsset",
            SourceRelativePath = "Assets/hero.png",
            ImportedRelativePath = Path.Combine(cache, "primary.other.ktx2"),
            TargetArtifacts = { [TextureBuildTarget.Pc] = Path.Combine(cache, "primary.pc.ktx2") },
        };
        Serializer.Save(Path.Combine(project, ".Cache", "assetCatalog.json"), new AssetCatalog { Records = [record] });

        BuildFinalizer.PackageAssets(Context(output, "project.csproj"));

        var reader = OapReader.OpenFile(Directory.GetFiles(output, "*.oap", SearchOption.AllDirectories).Single());
        Assert.Equal([2], reader.ReadAsset(reader.FindById(id)!.Value));
    }

    /// <summary>The desktop platform reports its texture target, runtime identifiers, toolchain and executable extension.</summary>
    [Fact]
    public void DesktopPlatformDescribesItsTargets()
    {
        var platform = new DesktopBuildPlatform();

        Assert.Equal(TextureBuildTarget.Pc, platform.TextureTarget);
        Assert.Null(platform.Devices);
        Assert.Equal(BuildPlatforms.DesktopRuntimeIdentifiers, platform.RuntimeIdentifiers);
        Assert.Equal(RuntimeDependencies.Current.TargetFramework, platform.TargetFramework);
        Assert.True(platform.CheckToolchain().IsReady);
    }
}
