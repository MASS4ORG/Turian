namespace Turian.Tests.Turian.Editor.Build;

/// <summary>
/// Test CsProjectGenerator
/// </summary>
public class BuildAppSetttingsTest
{
    readonly BuildAppSettings settings;

    /// <summary>
    /// Ctor
    /// </summary>
    public BuildAppSetttingsTest()
    {
        settings = new()
        {
            Title = "My App is: super cool",
            ProjectAbsoluteDir = "/folder/app"
        };
    }

    /// <summary>
    /// Validate the paths generated
    /// </summary>
    [Fact]
    public void CheckPaths()
    {
        Assert.Equal("/folder/app", settings.ProjectAbsoluteDir);
        Assert.Equal(Path.GetDirectoryName("/folder/app/Assets/"), settings.AssetsAbsoluteDir);
        Assert.Equal(Path.GetDirectoryName("/folder/app/.Cache/"), settings.CacheAbsoluteDir);
        Assert.Equal(Path.GetDirectoryName("../../Assets/"), settings.CacheSourceRelativeDir);
    }

    /// <summary>
    /// Validate the convertion of the app Title to something valid for paths
    /// </summary>
    [Fact]
    public void CheckTitle()
    {
        Assert.Equal("My App is  super cool", settings.TitleToPathFriendly);
    }

    /// <summary>
    /// Check the lists of packages
    /// </summary>
    [Fact]
    public void PackagesExists()
    {
        Assert.NotEmpty(settings.TurianPackages);
        Assert.NotEmpty(settings.InternalPackages);
        Assert.NotEmpty(settings.Packages);
        Assert.NotEmpty(settings.PackageReferences);
        Assert.NotEmpty(settings.TargetFramework);
        Assert.NotEmpty(settings.TargetSdk);
    }

    /// <summary>
    /// The in-game UI and its Guinevere reach a game through the UI brick, not the engine list, so a game without
    /// the brick ships neither.
    /// </summary>
    [Fact]
    public void EngineListsLeaveTheUiToItsBrick()
    {
        Assert.DoesNotContain(settings.TurianPackages,
            package => package.Item2 is "Guinevere" or "Turian.Engine.UI");
        Assert.DoesNotContain(settings.PackageReferences, package => package.Item1.StartsWith("SkiaSharp", StringComparison.Ordinal));
    }

    /// <summary>
    /// Try loading the settings from a BuildAppSettings
    /// </summary>
    [Fact]
    public void LoadCopiesTheProjectFolder()
    {
        var source = new BuildAppSettings
        {
            Title = "My App",
            ProjectAbsoluteDir = "/folder/app"
        };

        var destination = new BuildAppSettings().Load(source) as BuildAppSettings;

        Assert.NotNull(destination);
        Assert.Equal(source.ProjectAbsoluteDir, destination.ProjectAbsoluteDir);
        Assert.Equal(source.Title, destination.Title);
    }
}
