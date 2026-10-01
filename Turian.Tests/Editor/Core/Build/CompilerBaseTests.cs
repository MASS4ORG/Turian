namespace Turian.Tests;

/// <summary>Checks generated-project restore targeting and failure propagation.</summary>
public sealed class CompilerBaseTests : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("turian-restore-").FullName;

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>Restore uses the requested project even when obsolete projects share its folder.</summary>
    [Fact]
    public async Task RestoreTargetsOneProject()
    {
        var project = Path.Combine(directory, "Current.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
              <TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit>
            </PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Obsolete.csproj"), "not a valid project");
        File.WriteAllText(Path.Combine(directory, "NuGet.Config"),
            "<configuration><packageSources><clear /></packageSources></configuration>");

        Assert.Equal(project, await Compiler().Setup(project));

        Assert.True(File.Exists(Path.Combine(directory, "obj", "project.assets.json")));
    }

    /// <summary>A failed restore prevents setup from returning projects for compilation or cache reuse.</summary>
    [Fact]
    public async Task SetupPropagatesRestoreFailure()
    {
        var project = Path.Combine(directory, "Broken.csproj");
        File.WriteAllText(project, "not a valid project");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Compiler().Setup(project));
        Assert.Equal("Package restore failed.", exception.Message);
        Assert.False(File.Exists(Path.Combine(directory, "restore.stamp")));
    }

    TestCompiler Compiler()
    {
        var settings = new BuildAppSettings { ProjectAbsoluteDir = directory };
        Directory.CreateDirectory(settings.AssetsAbsoluteDir);
        settings.Loaded.Use(new PlayerSettings { StartupScene = new AssetReference<Prefab>(Guid.NewGuid()) });
        return new TestCompiler(settings);
    }

    sealed class TestCompiler(IAppSettings settings) : CompilerBase(settings, NullLogger.Instance)
    {
        public Task<string> Setup(string project) => SetupAsync(() => project);
    }
}
