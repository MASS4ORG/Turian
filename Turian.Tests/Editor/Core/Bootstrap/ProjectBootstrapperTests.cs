namespace Turian.Tests;

/// <summary>Verifies the scaffold a new project starts from.</summary>
public sealed class ProjectBootstrapperTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-bootstrap-{Guid.NewGuid():N}");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>The project folder is named after the title and holds the starter files.</summary>
    [Fact]
    public async Task CreatesStarterFiles()
    {
        var project = await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "My Game"));

        Assert.NotNull(project);
        var assets = Path.Combine(project, "Assets");
        Assert.True(File.Exists(Path.Combine(assets, "scene-01.prefab")));
        Assert.True(File.Exists(Path.Combine(assets, "Game.cs")));
    }

    /// <summary>Scripts see the engine, its attributes and the key codes without their own usings.</summary>
    [Fact]
    public async Task WritesGlobalUsings()
    {
        var project = await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "Game"));

        var globals = await File.ReadAllTextAsync(
            Path.Combine(project!, "Assets", "Globals.cs"), TestContext.Current.CancellationToken);
        Assert.Contains("global using Turian.Engine.Core;", globals, StringComparison.Ordinal);
        Assert.Contains("global using Turian;", globals, StringComparison.Ordinal);
        Assert.Contains("global using Silk.NET.Input;", globals, StringComparison.Ordinal);
    }

    /// <summary>
    /// A new project gets a project asset stamped with this engine, registering its settings and an active Client
    /// build profile.
    /// </summary>
    [Fact]
    public async Task RegistersSettingsAndAClientProfile()
    {
        var project = await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "Game"));
        var settings = new BuildAppSettings { ProjectAbsoluteDir = project! };

        var asset = ProjectAsset.Load(settings.AssetsAbsoluteDir)!;

        Assert.Equal(ProjectSettingsFiles.EngineVersionStamp, asset.EngineVersion);
        Assert.Equal(4, asset.Settings.Count);
        Assert.Equal(["PlayerSettings", "InputSettings", "GraphicsSettings", "TagsAndLayersSettings"],
            ProjectSettingsLoader.FindSources(settings.AssetsAbsoluteDir).Select(static s => s.Kind.Name));
        Assert.Equal("Client", BuildProfiles.Active(settings)!.Name);
        Assert.Empty(ProjectValidator.Validate(project));
    }

    /// <summary>A new project installs the default built-in packages, which ship beside the engine.</summary>
    [Fact]
    public async Task InstallsDefaultBuiltins()
    {
        var project = await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "Game"));

        var packages = ProjectPackages.Resolve(project!).Packages;

        Assert.Equal(ProjectPackages.DefaultBuiltins.Order(StringComparer.Ordinal),
            packages.Select(static p => p.Id).Order(StringComparer.Ordinal));
        Assert.All(packages, static p => Assert.Equal(PackageOrigin.Builtin, p.Origin));
    }
}
