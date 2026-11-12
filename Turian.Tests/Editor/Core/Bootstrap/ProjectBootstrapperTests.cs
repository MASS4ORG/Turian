namespace Turian.Tests;

/// <summary>Verifies the scaffold a new project starts from.</summary>
public sealed class ProjectBootstrapperTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-bootstrap-{Guid.NewGuid():N}");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
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

    /// <summary>
    /// A scaffold, even inside a repository folder that already exists, writes no workstation folder and no platform
    /// separator into any file, so it moves between directories and systems unchanged.
    /// </summary>
    [Fact]
    public async Task WritesPortablePaths()
    {
        var repository = Path.Combine(root, "Fresh Repository");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "# Game", TestContext.Current.CancellationToken);

        var project = await new ProjectBootstrapper().CreateAsync(repository);

        var files = Directory.EnumerateFiles(project!, "*", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
            .Where(static path => Path.GetExtension(path) is ".meta" or ".dataasset" or ".prefab" or ".json")
            .ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(project!, text, StringComparison.Ordinal);
            Assert.DoesNotContain(repository.Replace("\\", "\\\\", StringComparison.Ordinal), text, StringComparison.Ordinal);
            Assert.DoesNotMatch("\"RelativePath\":\\s*\"([A-Za-z]:|/|[^\"]*\\\\)", text);
        }
    }

    /// <summary>A new project loads from a different directory with every asset reference still resolving.</summary>
    [Fact]
    public async Task ScaffoldMovesBetweenDirectories()
    {
        var project = await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "Original"));
        var moved = Path.Combine(root, "Moved Elsewhere");

        Directory.Move(project!, moved);

        var settings = new BuildAppSettings { ProjectAbsoluteDir = moved };
        Assert.Empty(ProjectValidator.Validate(moved));
        Assert.Equal(4, ProjectSettingsLoader.FindSources(settings.AssetsAbsoluteDir).Count());
        Assert.Equal("Client", BuildProfiles.Active(settings)!.Name);
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
