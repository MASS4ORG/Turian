namespace Turian.Tests;

/// <summary>Checks CLI export failure reporting before scripts or assets are built.</summary>
[Collection(SerialTests.Name)]
public sealed class ExportCliTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-export-cli-").FullName;

    /// <summary>Removes the isolated project and brick store.</summary>
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    /// <summary>An invalid deployment returns an actionable error without a stack trace or compilation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeploymentFailureIsGracefulAndEarly(bool release)
    {
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "project")))!;
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
        File.WriteAllText(Path.Combine(project, "Assets", "Game.cs"), "invalid C# source");

        var result = await PrecastBrickFixture.RunCli(root, ["deploy", project, .. release ? (string[])["--release"] : []]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Select a device platform", result.Output);
        Assert.DoesNotContain("Unhandled exception", result.Output);
        Assert.DoesNotContain("Compiling", result.Output);
        Assert.DoesNotContain("Importing assets", result.Output);
        Assert.DoesNotContain("dbug:", result.Output);
        Assert.False(Directory.Exists(Path.Combine(project, ".Cache", "Source")));
    }

    /// <summary>Debug logging includes exception detail only when explicitly requested.</summary>
    [Fact]
    public async Task ExplicitDebugVerbosityIncludesDiagnosticDetails()
    {
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "project")))!;
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);

        var result = await PrecastBrickFixture.RunCli(root, "deploy", project, "--verbosity", "Debug");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("dbug:", result.Output);
        Assert.Contains("Command failed", result.Output);
        Assert.DoesNotContain("Unhandled exception", result.Output);
    }

    /// <summary>Preflight still permits a complete export from source, with default and named output locations.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "E2E")]
    public async Task ExportsAHeadlessProject(bool named)
    {
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "sample")))!;
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
        var settings = new AppSettings { Title = "sample", ProjectAbsoluteDir = project };
        ProjectSettingsFiles.CreateBuildProfile(settings,
            new BuildProfile { Name = "Server", Target = BuildTarget.Server }, active: true);

        var result = await PrecastBrickFixture.RunCli(root,
            ["export", project, .. named ? (string[])["--profile", "Server"] : []]);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("Export succeeded", result.Output);
        Assert.DoesNotContain("dbug:", result.Output);
        var output = named ? Path.Combine(project, "Output", "Server", "sample")
            : Path.Combine(project, "Output", "sample");
        Assert.True(File.Exists(Path.Combine(output, "sample")), result.Output);
    }
}
