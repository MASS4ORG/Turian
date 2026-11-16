using System.CommandLine;

namespace Turian.Tests;

/// <summary>Toolchain diagnostics report actionable failures without requiring a device or renderer.</summary>
[Collection(SerialTests.Name)]
public sealed class DoctorCliTests
{
    /// <summary>All platforms are checked, with deterministic output and a failed status for missing tools.</summary>
    [Fact]
    public void ReportsEveryPlatformAndFailsForMissingTools()
    {
        var desktop = Platform("desktop", true, "SDK ready");
        var android = Platform("android", false, "Install the Android workload");
        var (code, output) = Report([desktop, android]);

        Assert.Equal(1, code);
        Assert.Equal(new[]
        {
            "android: MISSING - Install the Android workload",
            "desktop: READY - SDK ready",
        }, output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        desktop.Received(1).CheckToolchain();
        android.Received(1).CheckToolchain();
    }

    /// <summary>A selected platform ignores unavailable tools for other targets and accepts case-insensitive ids.</summary>
    [Fact]
    public void ChecksOnlyTheRequestedPlatform()
    {
        var desktop = Platform("desktop", true, "SDK ready");
        var android = Platform("android", false, "Install the Android workload");
        var (code, output) = Report([desktop, android], " DESKTOP ");

        Assert.Equal(0, code);
        Assert.Contains("desktop: READY", output);
        Assert.DoesNotContain("android", output);
        android.DidNotReceive().CheckToolchain();
    }

    /// <summary>An absent target lists installed platforms and explains how to restore its provider.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailsForAnUninstalledPlatform(bool empty)
    {
        var (code, output) = Report(empty ? [] : [Platform("desktop", true, "SDK ready")], "android");

        Assert.Equal(1, code);
        Assert.Contains("No build platform 'android'", output);
        Assert.Contains("brick restore --project", output);
        if (!empty) { Assert.Contains("Installed: desktop", output); }
    }

    /// <summary>One failing detector cannot suppress the remaining platform results.</summary>
    [Fact]
    public void ContinuesAfterDetectorFailure()
    {
        var android = Platform("android", true, "unused");
        android.CheckToolchain().Returns(static _ => throw new IOException("SDK folder is unreadable"));
        var desktop = Platform("desktop", true, "SDK ready");
        var (code, output) = Report([android, desktop]);

        Assert.Equal(1, code);
        Assert.Contains("android: ERROR - SDK folder is unreadable", output);
        Assert.Contains("desktop: READY - SDK ready", output);
    }

    /// <summary>Installed toolchains yield a success result, including an omitted or whitespace filter.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void SucceedsForReadyPlatforms(string? platform)
    {
        var (code, _) = Report([Platform("desktop", true, "SDK ready")], platform);
        Assert.Equal(0, code);
    }

    /// <summary>Command parsing exposes the optional target and rejects an explicit non-project folder.</summary>
    [Fact]
    public async Task RejectsAnInvalidExplicitProject()
    {
        var directory = Directory.CreateTempSubdirectory("turian-doctor-");
        try
        {
            var factory = typeof(global::Turian.Editor.CLI.Program)
                .GetMethod("DoctorCommand", BindingFlags.Static | BindingFlags.NonPublic)!;
            var command = (Command)factory.Invoke(null, null)!;
            Assert.Equal(1, await new RootCommand { command }
                .Parse(["doctor", "android", "--project", directory.FullName])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(directory.EnumerateFileSystemInfos());
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>A standalone invocation can check desktop tools without creating or upgrading a project.</summary>
    [Fact]
    public async Task ChecksDesktopOutsideAProject()
    {
        var directory = Directory.CreateTempSubdirectory("turian-doctor-host-");
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory.FullName;
            var factory = typeof(global::Turian.Editor.CLI.Program)
                .GetMethod("DoctorCommand", BindingFlags.Static | BindingFlags.NonPublic)!;
            var command = (Command)factory.Invoke(null, null)!;
            Assert.Equal(0, await new RootCommand { command }.Parse(["doctor", "desktop"])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(directory.EnumerateFileSystemInfos());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            directory.Delete(true);
        }
    }

    /// <summary>Project discovery uses the committed lock and never silently repairs missing restore state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiresALockedProject(bool restored)
    {
        var directory = Directory.CreateTempSubdirectory("turian-doctor-project-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "Assets"));
            Directory.CreateDirectory(Path.Combine(directory.FullName, "Bricks"));
            var manifest = Path.Combine(directory.FullName, "Bricks", "manifest.json");
            File.WriteAllText(manifest, "{\"dependencies\":{\"org.mass4.turian.vulkan\":null}}");
            if (restored) { new LockFile().Save(directory.FullName); }
            var factory = typeof(global::Turian.Editor.CLI.Program)
                .GetMethod("DoctorCommand", BindingFlags.Static | BindingFlags.NonPublic)!;
            var command = (Command)factory.Invoke(null, null)!;
            Assert.Equal(restored ? 0 : 1, await new RootCommand { command }
                .Parse(["doctor", "desktop", "--project", directory.FullName])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("{\"dependencies\":{\"org.mass4.turian.vulkan\":null}}", File.ReadAllText(manifest));
            Assert.Equal(restored, File.Exists(Path.Combine(directory.FullName, "Bricks", "packages-lock.json")));
        }
        finally
        {
            ProjectPackages.Invalidate(directory.FullName);
            directory.Delete(true);
        }
    }

    static IBuildPlatform Platform(string id, bool ready, string message)
    {
        var platform = Substitute.For<IBuildPlatform>();
        platform.Id.Returns(id);
        platform.CheckToolchain().Returns(new BuildPlatformStatus(ready, message));
        return platform;
    }

    static (int Code, string Output) Report(IReadOnlyList<IBuildPlatform> platforms, string? id = null)
    {
        using var output = new StringWriter();
        var report = typeof(global::Turian.Editor.CLI.Program)
            .GetMethod("ReportToolchains", BindingFlags.Static | BindingFlags.NonPublic)!;
        var code = (int)report.Invoke(null, [platforms, id, output])!;
        return (code, output.ToString());
    }
}
