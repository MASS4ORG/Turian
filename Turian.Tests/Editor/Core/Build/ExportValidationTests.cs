namespace Turian.Tests;

/// <summary>Checks deployment selection and validation before export setup writes build inputs.</summary>
[Collection(SerialTests.Name)]
public sealed class ExportValidationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-export-validation-").FullName;

    /// <summary>Removes the isolated project.</summary>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A deployment to desktop fails before generating projects or restoring packages.</summary>
    [Fact]
    public async Task DesktopDeploymentFailsBeforeSetup()
    {
        var settings = new AppSettings { ProjectAbsoluteDir = root };
        var exporter = new ExportUserCode(settings, NullLogger.Instance, deployment: new BuildDeployment(null));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(exporter.ExecuteAsync);

        Assert.Contains("Select a device platform", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    /// <summary>Checking a desktop export succeeds without generating or restoring a project.</summary>
    [Fact]
    public void DesktopPreflightNeedsNoBuildInputs()
    {
        var settings = new AppSettings { ProjectAbsoluteDir = root };

        new ExportUserCode(settings, NullLogger.Instance).Validate();

        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    /// <summary>Local development retains settings references, RID and defines without mutating the saved profile.</summary>
    [Fact]
    public void DevelopmentProfileKeepsAuthoredSettings()
    {
        var profile = new BuildProfile
        {
            Name = "Android Client",
            Platform = "android",
            Configuration = BuildConfiguration.Release,
            RuntimeIdentifier = "android-arm64",
            ScriptingDefines = "CUSTOM",
            Settings = [new DataAssetReference<ProjectSettingsAsset>(Guid.NewGuid())],
        };

        var temporary = profile.ForDevelopment();

        Assert.False(profile.Development);
        Assert.True(temporary.Development);
        Assert.Equal(profile.Name, temporary.Name);
        Assert.Equal(profile.Platform, temporary.Platform);
        Assert.Equal(profile.Configuration, temporary.Configuration);
        Assert.Equal(profile.RuntimeIdentifier, temporary.RuntimeIdentifier);
        Assert.Contains("CUSTOM", temporary.Symbols);
        Assert.Contains(BuildProfile.DevelopmentSymbol, temporary.Symbols);
        Assert.Equal(profile.Settings, temporary.Settings);
        temporary.Settings.Clear();
        Assert.Single(profile.Settings);
    }
}
