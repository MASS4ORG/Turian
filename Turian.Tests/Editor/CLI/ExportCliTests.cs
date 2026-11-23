using System.Net;
using System.Net.Sockets;

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

        var (ExitCode, Output) = await PrecastBrickFixture.RunCli(root, ["deploy", project, .. release ? (string[])["--release"] : []]);

        Assert.Equal(1, ExitCode);
        Assert.Contains("Select a device platform", Output);
        Assert.DoesNotContain("Unhandled exception", Output);
        Assert.DoesNotContain("Compiling", Output);
        Assert.DoesNotContain("Importing assets", Output);
        Assert.DoesNotContain("dbug:", Output);
        Assert.False(Directory.Exists(Path.Combine(project, ".Cache", "Source")));
    }

    /// <summary>Debug logging includes exception detail only when explicitly requested.</summary>
    [Fact]
    public async Task ExplicitDebugVerbosityIncludesDiagnosticDetails()
    {
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "project")))!;
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);

        var (ExitCode, Output) = await PrecastBrickFixture.RunCli(root, "deploy", project, "--verbosity", "Debug");

        Assert.Equal(1, ExitCode);
        Assert.Contains("dbug:", Output);
        Assert.Contains("Command failed", Output);
        Assert.DoesNotContain("Unhandled exception", Output);
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

        var (ExitCode, Output) = await PrecastBrickFixture.RunCli(root,
            ["export", project, .. named ? (string[])["--profile", "Server"] : []]);

        Assert.True(ExitCode == 0, Output);
        Assert.Contains("Export succeeded", Output);
        Assert.DoesNotContain("dbug:", Output);
        var output = named ? Path.Combine(project, "Output", "Server", "sample")
            : Path.Combine(project, "Output", "sample");
        var executable = OperatingSystem.IsWindows() ? "sample.exe" : "sample";
        Assert.True(File.Exists(Path.Combine(output, executable)), Output);
    }

    /// <summary>Only an opted-in Development export exposes profile frames, even with identical launch settings.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "E2E")]
    public async Task HeadlessExportGatesPlayerProfilerByDevelopmentProfile(bool development)
    {
        var project = (await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "sample")))!;
        ProjectPackages.OptOutOfDefault(project, ProjectPackages.RenderBrick);
        var settings = new AppSettings { Title = "sample", ProjectAbsoluteDir = project };
        ProjectSettingsFiles.CreateBuildProfile(settings,
            new BuildProfile { Name = "Server", Target = BuildTarget.Server, Development = development }, active: true);
        var export = await PrecastBrickFixture.RunCli(root, "export", project, "--profile", "Server");
        Assert.True(export.ExitCode == 0, export.Output);

        var output = Path.Combine(project, "Output", "Server", "sample");
        var executable = Path.Combine(output, OperatingSystem.IsWindows() ? "sample.exe" : "sample");
        var port = AvailablePort();
        var token = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = output, RedirectStandardOutput = true };
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(root, "bundle");
        start.Environment[PlayerProfileProtocol.PortVariable] = port.ToString(CultureInfo.InvariantCulture);
        start.Environment[PlayerProfileProtocol.TokenVariable] = token;
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await WaitForPlayerAsync(process, timeout.Token);
            var request = new PlayerProfileRequest(PlayerProfileProtocol.Version, token, Guid.Empty, 0);
            if (development)
            {
                PlayerProfileResponse response;
                do
                {
                    response = await PlayerProfileClient.ReadAsync(port, request, timeout.Token);
                    Assert.Null(response.Error);
                    if (response.Frames.Length == 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
                    }
                } while (response.Frames.Length == 0);
                Assert.Equal(process.Id, response.Session?.ProcessId);
                Assert.Contains(response.Frames[0].Samples, static sample => sample.Name == "Scene Tick");
            }
            else
            {
                await Assert.ThrowsAsync<SocketException>(() =>
                    PlayerProfileClient.ReadAsync(port, request, timeout.Token));
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    static async Task WaitForPlayerAsync(Process process, CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Contains("running headless", StringComparison.Ordinal))
            {
                return;
            }
        }
        throw new InvalidOperationException("Exported player exited before it began ticking.");
    }

    static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
