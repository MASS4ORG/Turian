namespace Turian.Tests.Turian.Editor.Build;

/// <summary>
/// Test CsProjectGenerator
/// </summary>
public class CsProjectGeneratorTests
{
    readonly IBuildAppSettings settings;
    readonly ILogger logger;

    /// <summary>
    /// Ctor
    /// </summary>
    public CsProjectGeneratorTests()
    {
        settings = Substitute.For<IBuildAppSettings>();
        settings.Title.Returns("Test Project");
        settings.TitleToPathFriendly.Returns("Test_Project");
        var absPath = Path.GetFullPath("./project");
        var cachePath = Path.Combine(absPath, "cache");
        settings.ProjectAbsoluteDir.Returns(absPath);
        settings.CacheAbsoluteDir.Returns(cachePath);
        settings.SourceSubDir.Returns("Source");
        settings.PackageReferences.Returns([
            ("removePackage", "1.0.0"),
            ("removePackage2", "2.0.0")
            ]);
        settings.TurianPackages.Returns([
            ("Turian/Engine/Attributes", "Turian.Engine.Attributes"),
            ("Turian/Engine/Core", "Turian.Engine.Core"),
        ]);

        logger = Substitute.For<ILogger>();
    }

    /// <summary>
    /// Should create the CSProj
    /// </summary>
    [Fact]
    public void GenerateShouldReturnCorrectFilePath()
    {
        // Arrange
        var expectedFilePath = Path.Combine(settings.CacheAbsoluteDir, settings.SourceSubDir, "Test_Project.csproj");

        // Act
        var project = CsProjectGenerator.GenerateUserCode(settings, logger);

        // Assert
        Assert.Equal(expectedFilePath, project.FullPath);
    }

    /// <summary>
    /// Check if the PackageReference is present
    /// </summary>
    [Fact]
    public void GenerateShouldContainPackageReference()
    {
        // Arrange
        var expectedPackageName = settings.PackageReferences[0].Item1;
        var expectedVersion = settings.PackageReferences[0].Item2;

        // Act
        var project = CsProjectGenerator.GenerateUserCode(settings, logger);

        // Assert
        var packageReferences = project.Items
            .Where(i => i.ItemType == "PackageReference")
            .Select(i => new
            {
                Name = i.Include,
                Version = i.Metadata.FirstOrDefault(m => m.Name == "Version")?.Value
            })
            .ToList();

        Assert.Contains(packageReferences, p => p.Name == expectedPackageName && p.Version == expectedVersion);
    }

    /// <summary>
    /// Check if the PackageReference is present
    /// </summary>
    [Fact]
    public void GenerateShouldContainPackageReferenceString()
    {
        // Arrange
        var expectedPackageName = settings.PackageReferences[0].Item1;
        var expectedVersion = settings.PackageReferences[0].Item2;

        // Act
        var project = CsProjectGenerator.GenerateUserCode(settings, logger);

        var xmlContent = project.RawXml;

        // Assert
        var packageReferenceLine = xmlContent.Split('\n')
            .FirstOrDefault(line => line.Contains(
                $"<PackageReference Include=\"{expectedPackageName}\" Version=\"{expectedVersion}\"",
                StringComparison.InvariantCulture));

        // Assert
        Assert.NotNull(packageReferenceLine);
    }


    /// <summary>
    /// Check if the TurianPackages is present
    /// </summary>
    [Fact]
    public void GenerateShouldContainTurianPackages()
    {
        // Arrange
        var expectedPackageName = settings.TurianPackages[0].Item2;
        var expectedPath = Path.Combine(
            "$(TurianDir)",
            settings.TurianPackages[0].Item1,
            "bin",
            "Debug",
            settings.TargetFramework,
            $"{settings.TurianPackages[0].Item2}.dll"
        );

        // Act
        var project = CsProjectGenerator.GenerateUserCode(settings, logger);

        // Assert
        var turianPackages = project.Items
            .Where(i => i.ItemType == "Reference")
            .Select(i => new
            {
                Name = i.Include,
                HintPath = i.Metadata.FirstOrDefault(m => m.Name == "HintPath")?.Value
            })
            .ToList();

        Assert.Contains(turianPackages, p => p.Name == expectedPackageName && p.HintPath == expectedPath);
    }

    /// <summary>
    /// Check if the TurianPackages is present
    /// </summary>
    [Fact]
    public void GenerateShouldContainTurianPackagesString()
    {
        // Arrange
        var expectedPackageName = settings.TurianPackages[0].Item2;
        var expectedPath = Path.Combine(
            "$(TurianDir)",
            settings.TurianPackages[0].Item1,
            "bin",
            "Debug",
            settings.TargetFramework,
            $"{settings.TurianPackages[0].Item2}.dll"
        );

        // Act
        var project = CsProjectGenerator.GenerateUserCode(settings, logger);

        var xmlContent = project.RawXml;

        // Assert
        var packageReferenceLine = xmlContent.Split('\n')
            .FirstOrDefault(line => line.Contains(
                $"<Reference Include=\"{expectedPackageName}\" HintPath=\"{expectedPath}\"",
                StringComparison.InvariantCulture));

        // Assert
        Assert.NotNull(packageReferenceLine);
    }
}
