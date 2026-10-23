namespace Turian.Tests;

/// <summary>Registry commands preserve optional filters and display unrestricted registries as a wildcard.</summary>
[Trait("Category", "E2E")]
public sealed class BrickRegistryCliTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-registry-cli-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Adding a registry without scopes accepts all names, while an explicit filter survives saving.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("com.acme")]
    public async Task RegistryCommandsPreserveOptionalFilters(string? scope)
    {
        var project = Path.Combine(root, "game");
        var registry = Path.Combine(root, "registry");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(registry);
        File.WriteAllText(Path.Combine(registry, "index.json"), """{ "bricks": {}, "claims": {} }""");
        string[] arguments =
        [
            "brick", "registry", "add", registry, "--name", "local", "--allow-unsigned", "--project", project,
            .. scope is null ? (string[])[] : ["--scope", scope],
        ];

        var added = await Cli(arguments);

        Assert.True(added.ExitCode == 0, added.Output);
        Assert.Contains($"for {scope ?? "*"}", added.Output, StringComparison.Ordinal);
        var saved = Assert.Single(ProjectManifest.Load(project).Manifest.ScopedRegistries);
        Assert.Equal(scope is null ? [] : new[] { scope }, saved.Scopes);

        var listed = await Cli("brick", "registry", "list", "--project", project);
        Assert.True(listed.ExitCode == 0, listed.Output);
        Assert.Contains($"local  {registry}  scopes: {scope ?? "*"}", listed.Output, StringComparison.Ordinal);
    }

    /// <summary>Studio discovery keeps signing trust while allowing publishers outside the store's current claims.</summary>
    [Fact]
    public async Task StudioRegistryDiscoveryKeepsAllPublishers()
    {
        const string key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIETjciFivC4Q7z6o7KhiydqNS1Z5D6+ZZFbGUxQgL7ur test";
        var registry = Path.Combine(root, "registry");
        Directory.CreateDirectory(registry);
        File.WriteAllText(Path.Combine(registry, "index.json"),
            """{ "bricks": {}, "claims": { "com.acme": { "key": "k" } } }""");
        File.WriteAllText(Path.Combine(registry, "keys"),
            $$"""{ "keys": [ { "id": "k", "publicKey": "{{key}}" } ] }""");

        var added = await Cli("brick", "registry", "add", registry, "--studio");

        Assert.True(added.ExitCode == 0, added.Output);
        Assert.Contains("Trusting its key", added.Output, StringComparison.Ordinal);
        var saved = Assert.Single(ProjectManifest.Load(Path.Combine(root, "config", "studio")).Manifest.ScopedRegistries);
        Assert.Empty(saved.Scopes);
        Assert.True(saved.Serves("org.newpublisher.rules"));
        Assert.Equal(key, Assert.Single(saved.Keys));
        var listed = await Cli("brick", "registry", "list", "--studio");
        Assert.True(listed.ExitCode == 0, listed.Output);
        Assert.Contains($"registry  {registry}  scopes: *", listed.Output, StringComparison.Ordinal);
    }

    async Task<(int ExitCode, string Output)> Cli(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(global::Turian.Editor.CLI.Program).Assembly.Location);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[PackageStore.StoreVariable] = Path.Combine(root, "store");
        start.Environment[GayaConfig.HomeVariable] = Path.Combine(root, "config");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output + await error);
    }
}
