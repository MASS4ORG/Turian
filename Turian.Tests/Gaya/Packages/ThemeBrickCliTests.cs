namespace Turian.Tests;

/// <summary>Theme bricks from the command line: scaffold, verify, and install into the studio.</summary>
public sealed class ThemeBrickCliTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-theme-cli-{Guid.NewGuid():N}");

    /// <summary>Creates the working folder.</summary>
    public ThemeBrickCliTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A scaffolded theme brick verifies clean, installs into the studio and lists there.</summary>
    [Fact]
    public async Task ScaffoldVerifyAndInstallIntoTheStudio()
    {
        var created = await Cli("brick", "new", "user.you.night", "--kind", "theme", "--path", root);
        Assert.Equal(0, created.ExitCode);
        var brick = Path.Combine(root, "user.you.night");
        Assert.True(File.Exists(Path.Combine(brick, "Themes", "night.pss")));
        Assert.False(Directory.Exists(Path.Combine(brick, "Runtime")));

        var verified = await Cli("theme", "verify", brick);
        Assert.Equal(0, verified.ExitCode);
        Assert.Contains("Theme sheets are valid", verified.Output);

        var added = await Cli("brick", "add", "user.you.night", $"file:{brick}", "--studio");
        Assert.True(added.ExitCode == 0, added.Output);
        Assert.Contains("into the studio", added.Output);
        Assert.True(File.Exists(Path.Combine(root, "config", "studio", "Bricks", ProjectManifest.FileName)));

        var listed = await Cli("brick", "list", "--studio");
        Assert.Contains("user.you.night 0.1.0", listed.Output);
        Assert.Equal(0, (await Cli("brick", "restore", "--studio")).ExitCode);

        var removed = await Cli("brick", "remove", "user.you.night", "--studio");
        Assert.Equal(0, removed.ExitCode);
        Assert.Equal(1, (await Cli("brick", "remove", "user.you.night", "--studio")).ExitCode);
    }

    /// <summary>A broken sheet fails verification with its location, and a missing path is reported.</summary>
    [Fact]
    public async Task VerifyReportsLocatedErrors()
    {
        var sheet = Path.Combine(root, "broken.pss");
        File.WriteAllText(sheet, "@const theme-id = \"x.broken\";\n@import \"gaya.base\";\n$accent = mix(;\n");

        var result = await Cli("theme", "verify", sheet);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"{sheet}:3:", result.Output);
        Assert.Equal(2, (await Cli("theme", "verify", Path.Combine(root, "missing"))).ExitCode);
    }

    async Task<(int ExitCode, string Output)> Cli(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(global::Turian.Editor.CLI.Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment[PackageStore.StoreVariable] = Path.Combine(root, "store");
        start.Environment[GayaConfig.HomeVariable] = Path.Combine(root, "config");

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
