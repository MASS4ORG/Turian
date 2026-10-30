namespace Gaya.Tests;

/// <summary>Git dependencies materialize detached producer files without retaining credentials or repository metadata.</summary>
[Collection(SerialTests.Name)]
public sealed class BrickMaterializerGitTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("brick-git-materialize-").FullName;
    readonly string? previousConfig = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");

    /// <inheritdoc/>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousConfig);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>An HTTPS source resolves the exact commit and leaves no credentials in producer files.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitFetchesPinnedProducer(bool authenticated)
    {
        var commit = await Repository();
        var source = new BrickMaterializationSource
        {
            Type = "git",
            Url = "https://gitlab.example/team/producer.git",
            Ref = commit,
            Path = "producer",
        };
        var destination = Path.Combine(root, "output");
        await new BrickMaterializer().MaterializeAsync(source, destination, token: authenticated ? "fixture-read-token" : null,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("producer", File.ReadAllText(Path.Combine(destination, "Producer.cs")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".git")));
        Assert.DoesNotContain("fixture-read-token", string.Join("", Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)), StringComparison.Ordinal);
    }

    /// <summary>An unknown commit fails without creating an output directory or retaining staging data.</summary>
    [Fact]
    public async Task MissingCommitsFailCleanly()
    {
        _ = await Repository();
        var source = new BrickMaterializationSource
        {
            Type = "git",
            Url = "https://gitlab.example/team/producer.git",
            Ref = new string('a', 40),
            Path = "producer",
        };
        var error = await Assert.ThrowsAsync<PackageException>(() => new BrickMaterializer().MaterializeAsync(source,
            Path.Combine(root, "output"), token: "fixture-read-token", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Git could not materialize", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-read-token", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "output")));
        Assert.Empty(Directory.GetDirectories(root, ".materialize-*"));
    }

    async Task<string> Repository()
    {
        var repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(repository);
        new PackageManifest { Name = "com.example.producer", Version = SemanticVersion.Parse("1.0.0") }.Save(repository);
        File.WriteAllText(Path.Combine(repository, "Producer.cs"), "producer");
        await Git("init", repository);
        await Git("-C", repository, "add", ".");
        await Git("-C", repository, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.com",
            "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
        var commit = (await Git("-C", repository, "rev-parse", "HEAD")).Trim();
        var config = Path.Combine(root, "gitconfig");
        File.WriteAllText(config, $"[url \"{new Uri(repository + "/").AbsoluteUri}\"]\n\tinsteadOf = https://gitlab.example/team/producer.git\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
        return commit;
    }

    static async Task<string> Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
