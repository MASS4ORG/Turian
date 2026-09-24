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
    /// The engine assemblies reach a generated game as bare <c>&lt;Reference HintPath&gt;</c> entries,
    /// so nothing they depend on flows in transitively. Guinevere is the case that bit: when it was
    /// missing from the shipping list the game loaded, then threw
    /// <see cref="FileNotFoundException"/> out of the UI overlay on every rendered frame.
    /// </summary>
    [Fact]
    public void TurianPackagesShipGuinevereAlongsideEngineUi()
    {
        Assert.Contains(
            typeof(UiDocumentComponent).Assembly.GetReferencedAssemblies(),
            reference => reference.Name == "Guinevere");

        Assert.Contains(
            settings.TurianPackages,
            package => string.Equals(package.Item2, "Guinevere", StringComparison.Ordinal));
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
