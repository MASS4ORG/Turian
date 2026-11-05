namespace Turian.Tests;

/// <summary>Windows CI verifies NSIS availability independently of Chocolatey's exit code.</summary>
public sealed class NsisSetupTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-nsis-setup-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A successful package-manager exit without a compiler is retried and ultimately fails setup.</summary>
    [Fact]
    public async Task MissingCompilerFailsAfterSuccessfulChocolateyExits()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NSIS setup runs on Windows CI.");
        var result = await RunAsync("exit /b 0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(3, File.ReadAllLines(Path.Combine(root, "attempts.txt")).Length);
        Assert.Contains("NSIS setup failed after 3 attempts", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "github-path.txt")));
    }

    /// <summary>A transient failed download recovers when a later attempt actually installs the compiler.</summary>
    [Fact]
    public async Task RetryExportsTheCompilerDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NSIS setup runs on Windows CI.");
        var result = await RunAsync("""
            if not exist "%NSIS_TEST_ROOT%\retry.flag" (
                echo retry>"%NSIS_TEST_ROOT%\retry.flag"
                exit /b 1
            )
            mkdir "%NSIS_TEST_ROOT%\program-files\NSIS\Bin"
            type nul > "%NSIS_TEST_ROOT%\program-files\NSIS\Bin\makensis.exe"
            exit /b 0
            """);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "attempts.txt")).Length);
        Assert.Equal([Path.Combine(root, "program-files", "NSIS", "Bin")],
            File.ReadAllLines(Path.Combine(root, "github-path.txt")));
    }

    /// <summary>An installed compiler is usable when Chocolatey reports that a restart is needed.</summary>
    [Fact]
    public async Task InstalledCompilerAcceptsRestartExitCode()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NSIS setup runs on Windows CI.");
        var result = await RunAsync("""
            mkdir "%NSIS_TEST_ROOT%\program-files\NSIS"
            type nul > "%NSIS_TEST_ROOT%\program-files\NSIS\makensis.exe"
            exit /b 3010
            """);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Single(File.ReadAllLines(Path.Combine(root, "attempts.txt")));
    }

    /// <summary>Existing installations avoid the package feed and remain available to subsequent CI steps.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Bin")]
    public async Task ExistingCompilerAvoidsChocolatey(string subdirectory)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NSIS setup runs on Windows CI.");
        var compilerDirectory = Path.Combine(root, "program-files", "NSIS", subdirectory);
        Directory.CreateDirectory(compilerDirectory);
        await File.WriteAllTextAsync(Path.Combine(compilerDirectory, "makensis.exe"), string.Empty,
            TestContext.Current.CancellationToken);
        var result = await RunAsync("exit /b 1");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(File.Exists(Path.Combine(root, "attempts.txt")));
        Assert.Equal([compilerDirectory], File.ReadAllLines(Path.Combine(root, "github-path.txt")));
    }

    async Task<(int ExitCode, string Output)> RunAsync(string installBody)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        await File.WriteAllTextAsync(Path.Combine(bin, "choco.cmd"),
            "@echo off\r\necho attempt>>\"%NSIS_TEST_ROOT%\\attempts.txt\"\r\n" + installBody,
            TestContext.Current.CancellationToken);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Turian.slnx")))
        {
            repository = repository.Parent;
        }

        Assert.NotNull(repository);
        var wrapper = Path.Combine(root, "setup.ps1");
        await File.WriteAllTextAsync(wrapper, """
            $env:ProgramFiles = Join-Path $env:NSIS_TEST_ROOT 'program-files'
            ${env:ProgramFiles(x86)} = Join-Path $env:NSIS_TEST_ROOT 'program-files-x86'
            & $env:NSIS_SETUP_SCRIPT -Attempts 3 -RetryDelaySeconds 0
            """, TestContext.Current.CancellationToken);
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                     wrapper })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var key in start.Environment.Keys.Where(static key =>
                     new[] { "PATH", "PATHEXT", "ProgramFiles", "ProgramFiles(x86)", "GITHUB_PATH", "NSIS_TEST_ROOT" }
                         .Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["PATH"] = bin;
        start.Environment["PATHEXT"] = ".EXE;.CMD;.BAT";
        start.Environment["ProgramFiles"] = Path.Combine(root, "program-files");
        start.Environment["ProgramFiles(x86)"] = Path.Combine(root, "program-files-x86");
        start.Environment["GITHUB_PATH"] = Path.Combine(root, "github-path.txt");
        start.Environment["NSIS_TEST_ROOT"] = root;
        start.Environment["NSIS_SETUP_SCRIPT"] = Path.Combine(repository.FullName, "ci", "install-nsis.ps1");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output + await error);
    }
}
