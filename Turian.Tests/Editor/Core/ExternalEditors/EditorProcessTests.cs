namespace Turian.Tests;

/// <summary>Starting an editor reports a program that cannot start or fails at once, without blocking.</summary>
public sealed class EditorProcessTests
{
    static EditorCommand Dotnet(params string[] arguments) => new(DotnetLocator.Find(), arguments);

    /// <summary>A launcher that exits cleanly is a success.</summary>
    [Fact]
    public async Task CleanExitIsSuccess() =>
        await EditorProcess.StartAsync(Dotnet("--version"), "Dotnet", TestContext.Current.CancellationToken);

    /// <summary>A launcher that exits with an error inside the grace period is a failure naming the editor.</summary>
    [Fact]
    public async Task ErrorExitIsFailure()
    {
        var command = Dotnet("turian-no-such-command");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => EditorProcess.StartAsync(
            command, "Dotnet", TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30)));

        Assert.Contains("Dotnet", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A program that does not exist is a failure with the editor's name, not a raw exception.</summary>
    [Fact]
    public async Task MissingProgramIsFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"turian-missing-{Guid.NewGuid():N}");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => EditorProcess.StartAsync(
            new EditorCommand(missing, []), "Ghost", TestContext.Current.CancellationToken));

        Assert.Contains("Ghost", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The command carries each argument separately, so paths never need quoting.</summary>
    [Fact]
    public void StartInfoKeepsArgumentsSeparate()
    {
        var start = new EditorCommand("edit", ["--goto", "/my game/é.cs:3:1"]).ToStartInfo();

        Assert.False(start.UseShellExecute);
        Assert.Equal(["--goto", "/my game/é.cs:3:1"], start.ArgumentList);
    }
}
