namespace Turian.Tests;

/// <summary>
/// Resolving a project's packages from local folders, embedded folders and real git repositories created on the
/// fly, to any dependency depth.
/// </summary>
public sealed class PackageResolverTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-packages-{Guid.NewGuid():N}");
    readonly string project;
    readonly PackageStore store;

    /// <summary>Creates an empty project and store.</summary>
    public PackageResolverTests()
    {
        project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        store = new PackageStore(Path.Combine(root, "store"));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>A package's own dependencies are fetched from the sources it names, to any depth.</summary>
    [Fact]
    public async Task DependenciesResolveTransitively()
    {
        Package("rules", "com.acme.rules", "1.0.0");
        Package("inventory", "com.acme.inventory", "1.1.0", ("com.acme.rules", "file:../rules"));
        Package("shop", "com.acme.shop", "2.0.0", ("com.acme.inventory", "file:../inventory"), ("com.acme.rules", "^1.0.0"));
        Manifest(("com.acme.shop", "file:../../shop"));

        var resolution = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(["com.acme.rules", "com.acme.inventory", "com.acme.shop"], resolution.Packages.Select(p => p.Id));
        Assert.Equal([3, 2, 1], resolution.Packages.Select(p => p.Depth));
    }

    /// <summary>A version range nobody satisfies fails, naming who asked.</summary>
    [Fact]
    public async Task UnsatisfiedRangesFail()
    {
        Package("rules", "com.acme.rules", "1.0.0");
        Package("shop", "com.acme.shop", "2.0.0", ("com.acme.rules", "^2.0.0"));
        Manifest(("com.acme.shop", "file:../../shop"), ("com.acme.rules", "file:../../rules"));

        var error = await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));
        Assert.Contains("com.acme.shop requires com.acme.rules ^2.0.0", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A range with no provider asks for the package to be declared.</summary>
    [Fact]
    public async Task MissingProvidersFail()
    {
        Package("shop", "com.acme.shop", "2.0.0", ("com.acme.rules", "^1.0.0"));
        Manifest(("com.acme.shop", "file:../../shop"));

        var error = await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));
        Assert.Contains("nothing provides", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A folder whose package id differs from the declared one is refused.</summary>
    [Fact]
    public async Task MismatchedIdsFail()
    {
        Package("rules", "com.acme.rules", "1.0.0");
        Manifest(("com.acme.other", "file:../../rules"));

        await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));
    }

    /// <summary>An embedded package wins over the declared source.</summary>
    [Fact]
    public async Task EmbeddedPackagesWin()
    {
        Package("rules", "com.acme.rules", "1.0.0");
        Package(Path.Combine("project", "Packages", "rules"), "com.acme.rules", "1.0.1-fork");
        Manifest(("com.acme.rules", "file:../../rules"));

        var package = Assert.Single((await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken)).Packages);

        Assert.Equal(PackageOrigin.Embedded, package.Origin);
        Assert.Equal("1.0.1-fork", package.Version.ToString());
    }

    /// <summary>The user override remaps a package for one machine and marks the resolution as local.</summary>
    [Fact]
    public async Task UserOverridesRemapPackages()
    {
        Package("rules", "com.acme.rules", "1.0.0");
        Package("rules-dev", "com.acme.rules", "1.1.0-dev");
        Manifest(("com.acme.rules", "file:../../rules"));
        File.WriteAllText(Path.Combine(project, "Packages", ProjectManifest.UserFileName),
            """{ "dependencies": { "com.acme.rules": "file:../../rules-dev" } }""");

        var resolution = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal("1.1.0-dev", Assert.Single(resolution.Packages).Version.ToString());
        Assert.True(resolution.UsesUserOverride);
    }

    /// <summary>Packages built for another host, or another host version, are refused.</summary>
    [Fact]
    public async Task EnginesAreChecked()
    {
        var path = Package("rules", "com.acme.rules", "1.0.0");
        var manifest = PackageManifest.Load(path);
        manifest.Engines["turian"] = VersionRange.Parse("^2.0.0");
        manifest.Save(path);
        Manifest(("com.acme.rules", "file:../../rules"));

        await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));
    }

    /// <summary>Categories must be prefixed by the package, one of its dependencies or a reserved prefix.</summary>
    [Fact]
    public void CategoryPrefixesAreChecked()
    {
        var path = Package("theme", "com.acme.theme", "1.0.0");
        var manifest = PackageManifest.Load(path);
        manifest.Categories = ["gaya:theme", "com.acme.theme:dark"];
        manifest.Save(path);
        Assert.Equal(2, PackageManifest.Load(path, ["gaya"]).Categories.Count);

        manifest.Categories = ["com.roblox:content"];
        manifest.Save(path);
        Assert.Throws<PackageException>(() => PackageManifest.Load(path, ["gaya"]));
    }

    /// <summary>
    /// A git source resolves its tag to a commit, extracts it read-only into the store, and stays on the locked
    /// commit when the tag later moves, until an update is asked for.
    /// </summary>
    [Fact]
    public async Task GitSourcesAreLockedToACommit()
    {
        var repository = Package("rules-repo", "com.acme.rules", "1.0.0");
        Git(repository, "init", "--quiet", "--initial-branch=main");
        var first = Commit(repository, "v1.0.0");
        Manifest(("com.acme.rules", $"git+file://{repository}#v1.0.0"));

        var resolution = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);
        var package = Assert.Single(resolution.Packages);
        Assert.Equal(first, package.Commit);
        Assert.StartsWith(store.Root, package.RootPath, StringComparison.Ordinal);
        Assert.True(File.GetAttributes(Path.Combine(package.RootPath, PackageManifest.FileName)).HasFlag(FileAttributes.ReadOnly));
        resolution.Lock.Save(project);

        var manifest = PackageManifest.Load(repository);
        manifest.Version = SemanticVersion.Parse("1.0.1");
        manifest.Save(repository);
        var second = Commit(repository, "v1.0.0", force: true);

        Assert.Equal(first, Assert.Single((await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken)).Packages).Commit);
        Assert.Equal(first, Assert.Single((await Resolver(locked: true).ResolveAsync(project, TestContext.Current.CancellationToken)).Packages).Commit);

        var updated = Assert.Single((await new PackageResolver(store, new PackageResolverOptions { Update = null })
            .ResolveAsync(project, TestContext.Current.CancellationToken)).Packages);
        Assert.Equal(second, updated.Commit);
        Assert.Equal("1.0.1", updated.Version.ToString());
    }

    /// <summary>Installing locked fails when the manifest asks for something the lock does not record.</summary>
    [Fact]
    public async Task LockedInstallsFailOnDrift()
    {
        Package("rules", "com.acme.rules", "1.0.0");
        Manifest(("com.acme.rules", "file:../../rules"));
        (await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken)).Lock.Save(project);

        Package("extra", "com.acme.extra", "1.0.0");
        Manifest(("com.acme.rules", "file:../../rules"), ("com.acme.extra", "file:../../extra"));

        await Assert.ThrowsAsync<PackageException>(() => Resolver(locked: true).ResolveAsync(project, TestContext.Current.CancellationToken));
    }

    PackageResolver Resolver(bool locked = false) => new(store, new PackageResolverOptions
    {
        Hosts = new Dictionary<string, SemanticVersion> { ["turian"] = SemanticVersion.Parse("1.2.0") },
        Locked = locked,
    });

    string Package(string folder, string id, string version, params (string Id, string Spec)[] dependencies)
    {
        var path = Path.Combine(root, folder);
        Directory.CreateDirectory(path);
        new PackageManifest
        {
            Name = id,
            Version = SemanticVersion.Parse(version),
            Dependencies = dependencies.ToDictionary(static d => d.Id, static d => d.Spec),
        }.Save(path);
        return path;
    }

    void Manifest(params (string Id, string Spec)[] dependencies) =>
        new ProjectManifest { Dependencies = dependencies.ToDictionary(static d => d.Id, static d => (string?)d.Spec) }
            .Save(project);

    static string Commit(string repository, string tag, bool force = false)
    {
        Git(repository, "add", "-A");
        Git(repository, "-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", tag);
        Git(repository, force ? ["tag", "-f", tag] : ["tag", tag]);
        return Git(repository, "rev-parse", "HEAD").Trim();
    }

    static string Git(string directory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output;
    }
}
