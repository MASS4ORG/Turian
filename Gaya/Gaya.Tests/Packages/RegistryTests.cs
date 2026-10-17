using System.Net;

namespace Gaya.Tests;

/// <summary>A static registry: publishing signed bricks into it, and resolving, locking and updating from it.</summary>
[Collection(SerialTests.Name)]
public sealed class RegistryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-registry-{Guid.NewGuid():N}");
    readonly string registry;
    readonly string project;
    readonly PackageStore store;
    readonly SshKeygenSigner registrySigner;
    readonly SshKeygenSigner mateo;
    readonly SshKeygenSigner other;

    /// <summary>Creates keys, an empty registry and a project scoped to it.</summary>
    public RegistryTests()
    {
        Directory.CreateDirectory(root);
        if (!KeygenAvailable())
        {
            Assert.Skip("ssh-keygen is not installed");
        }

        registry = Path.Combine(root, "registry");
        project = Path.Combine(root, "game");
        Directory.CreateDirectory(project);
        store = new PackageStore(Path.Combine(root, "store"));
        registrySigner = Key("registry");
        mateo = Key("mateo");
        other = Key("other");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>A project resolves a range from the registry, with its dependencies, and pins what it chose in the lock.</summary>
    [Fact]
    public async Task RangesResolveFromTheRegistryAndAreLocked()
    {
        Publish("user.mateo.rules", "1.0.0");
        Publish("user.mateo.rules", "1.1.0");
        Publish("user.mateo.rules", "2.0.0");
        Publish("user.mateo.shop", "1.0.0", ("user.mateo.rules", "^1.0.0"));
        Scope(("user.mateo.shop", "^1.0.0"));

        var resolution = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(["user.mateo.rules", "user.mateo.shop"], resolution.Packages.Select(static p => p.Id));
        var rules = resolution.Packages[0];
        Assert.Equal("1.1.0", rules.Version.ToString());
        Assert.Equal(PackageOrigin.Registry, rules.Origin);
        Assert.True(rules.IsReadOnly);
        Assert.Equal([2, 1], resolution.Packages.Select(static p => p.Depth));
        var entry = resolution.Lock.Dependencies["user.mateo.rules"];
        Assert.Equal(SshSignature.Fingerprint(registrySigner.PublicKey), entry.SignedBy);
        Assert.NotNull(entry.Integrity);

        resolution.Lock.Save(project);
        Directory.Delete(registry, recursive: true);
        var offline = await Resolver(locked: true).ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal(["user.mateo.rules", "user.mateo.shop"], offline.Packages.Select(static p => p.Id));
    }

    /// <summary>A locked project stays on its version until asked to update, which takes the newest one that fits.</summary>
    [Fact]
    public async Task UpdatesMoveTheLock()
    {
        Publish("user.mateo.rules", "1.0.0");
        Scope(("user.mateo.rules", "^1.0.0"));
        var first = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);
        first.Lock.Save(project);

        Publish("user.mateo.rules", "1.2.0");
        var kept = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("1.0.0", kept.Packages.Single().Version.ToString());

        var updated = await Resolver(updateAll: true).ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("1.2.0", updated.Packages.Single().Version.ToString());
    }

    /// <summary>Yanked versions and ones needing a newer engine are never chosen for a new resolution, yet a lock keeps a yanked one.</summary>
    [Fact]
    public async Task YankedAndIncompatibleVersionsAreSkipped()
    {
        Publish("user.mateo.rules", "1.0.0");
        Publish("user.mateo.rules", "1.1.0");
        Scope(("user.mateo.rules", "^1.0.0"));
        var before = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("1.1.0", before.Packages.Single().Version.ToString());
        before.Lock.Save(project);

        Publish("user.mateo.rules", "1.2.0", engine: ">=9.0.0");
        RegistryPublisher.Yank(registry, "user.mateo.rules", "1.1.0");

        var kept = await Resolver(updateAll: false).ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("1.1.0", kept.Packages.Single().Version.ToString());

        File.Delete(Path.Combine(project, "Bricks", LockFile.FileName));
        var fresh = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("1.0.0", fresh.Packages.Single().Version.ToString());
    }

    /// <summary>A signature from a key the project does not trust, a changed file and an unsigned entry are all refused.</summary>
    [Fact]
    public async Task UntrustedBricksAreRefused()
    {
        Publish("user.mateo.rules", "1.0.0");
        Scope(("user.mateo.rules", "^1.0.0"), trusted: other.PublicKey);
        var wrongKey = await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));
        Assert.Contains("was not made by any of the 1 key(s)", wrongKey.Message, StringComparison.Ordinal);

        Scope(("user.mateo.rules", "^1.0.0"));
        var file = Directory.GetFiles(Path.Combine(registry, "v1", "bricks"), "*.brick", SearchOption.AllDirectories).Single();
        File.AppendAllText(file, "tampered");
        var changed = await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));
        Assert.Contains("does not match the hash", changed.Message, StringComparison.Ordinal);

        Unsign();
        var unsigned = await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));
        Assert.Contains("is not signed", unsigned.Message, StringComparison.Ordinal);
    }

    /// <summary>A registry the project runs itself may be marked to accept unsigned bricks.</summary>
    [Fact]
    public async Task UnsignedBricksAreAcceptedOnlyWhenAllowed()
    {
        Publish("user.mateo.rules", "1.0.0");
        Unsign();
        Scope(("user.mateo.rules", "^1.0.0"), allowUnsigned: true);

        var package = Assert.Single((await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken)).Packages);

        Assert.Null(package.SignedBy);
    }

    /// <summary>The first publish under <c>user.&lt;name&gt;</c> claims it; only that key publishes there, and versions are immutable.</summary>
    [Fact]
    public void NamesAreClaimedByTheirFirstPublisher()
    {
        Publish("user.mateo.rules", "1.0.0");

        var index = RegistryIndex.Parse(File.ReadAllText(Path.Combine(registry, "v1", "index.json")));
        Assert.Equal(SshSignature.Fingerprint(mateo.PublicKey), index.Claims["user.mateo"].Key);

        Assert.Throws<PackageException>(() => Publish("user.mateo.shop", "1.0.0", publisher: other));
        Assert.Throws<PackageException>(() => Publish("user.mateo.rules", "1.0.0"));
        Assert.Throws<PackageException>(() => Publish("com.acme.rules", "1.0.0"));
        Publish("com.acme.rules", "1.0.0", publisher: other, claim: "com.acme");
        Assert.Throws<PackageException>(() => Publish("com.acme.shop", "1.0.0", publisher: mateo));
        Assert.Throws<PackageException>(() => Publish("com.acme.shop", "1.0.0", publisher: null));
    }

    /// <summary>The registry works over HTTP too, and an entitled brick needs a token, which only that registry's host receives.</summary>
    [Fact]
    public async Task HttpRegistriesHonourEntitlementTokens()
    {
        Publish("user.mateo.paid", "1.0.0", entitlement: "token");
        var seen = new List<string?>();
        var handler = new FileHandler(Path.Combine(registry, "v1"), request => seen.Add(request.Headers.Authorization?.Parameter));
        var options = new PackageResolverOptions { Http = new HttpClient(handler) };
        var registryName = "paid-" + Guid.NewGuid().ToString("N")[..6];
        new ProjectManifest
        {
            Dependencies = { ["user.mateo.paid"] = "^1.0.0" },
            ScopedRegistries = [new ScopedRegistry { Name = registryName, Url = "https://bricks.example.test/v1", Scopes = ["user.mateo"], Keys = [registrySigner.PublicKey] }],
        }.Save(project);

        var refused = await Assert.ThrowsAsync<PackageException>(() => new PackageResolver(store, options).ResolveAsync(project, TestContext.Current.CancellationToken));
        Assert.Contains("needs an access token", refused.Message, StringComparison.Ordinal);

        var variable = $"GAYA_REGISTRY_TOKEN_{registryName.ToUpperInvariant().Replace('-', '_')}";
        Environment.SetEnvironmentVariable(variable, "secret");
        seen.Clear();
        try
        {
            var package = Assert.Single((await new PackageResolver(store, options).ResolveAsync(project, TestContext.Current.CancellationToken)).Packages);
            Assert.Equal("user.mateo.paid", package.Id);
            Assert.All(seen, token => Assert.Equal("secret", token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>A registry whose address cannot be reached is read from its mirror; when every address fails, the address's error is reported.</summary>
    [Fact]
    public async Task MirrorsServeWhenTheAddressCannotBeReached()
    {
        Publish("user.mateo.rules", "1.0.0");
        var gone = Path.Combine(root, "gone");
        Registries(("user.mateo.rules", "^1.0.0"), new ScopedRegistry
        {
            Name = "test",
            Url = gone,
            Mirrors = [RegistryPublisher.LocationOf(registry)],
            Scopes = ["user.mateo"],
            Keys = [registrySigner.PublicKey],
        });

        var package = Assert.Single((await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken)).Packages);
        Assert.Equal("registry:test", package.Source);

        Directory.Delete(registry, recursive: true);
        var failed = await Assert.ThrowsAsync<PackageException>(() => Resolver(updateAll: true).ResolveAsync(project, TestContext.Current.CancellationToken));
        Assert.Contains(gone, failed.Message, StringComparison.Ordinal);
    }

    /// <summary>Of two registries scoped to the same names, a brick comes from the first that holds it, and stays there once locked.</summary>
    [Fact]
    public async Task OverlappingRegistriesFallThroughToTheOneHoldingTheBrick()
    {
        var local = Path.Combine(root, "local");
        Publish("user.mateo.shop", "1.0.0", into: local);
        Publish("user.mateo.rules", "1.0.0");
        ScopedRegistry Named(string name, string folder) => new()
        {
            Name = name,
            Url = RegistryPublisher.LocationOf(folder),
            Scopes = ["user.mateo"],
            Keys = [registrySigner.PublicKey],
        };
        Registries(("user.mateo.rules", "^1.0.0"), Named("local", local), Named("public", registry));

        var resolution = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("registry:public", Assert.Single(resolution.Packages).Source);
        resolution.Lock.Save(project);

        Publish("user.mateo.rules", "1.0.0", into: local);
        var locked = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("registry:public", Assert.Single(locked.Packages).Source);
    }

    void Registries((string Id, string Range) dependency, params ScopedRegistry[] registries) =>
        new ProjectManifest { Dependencies = { [dependency.Id] = dependency.Range }, ScopedRegistries = [.. registries] }.Save(project);

    PackageResolver Resolver(bool locked = false, bool updateAll = false) => new(store, new PackageResolverOptions
    {
        Hosts = new Dictionary<string, SemanticVersion> { ["engine"] = SemanticVersion.Parse("1.2.0") },
        ReservedCategoryPrefixes = ["engine"],
        Locked = locked,
        Update = updateAll ? null : new HashSet<string>(),
    });

    void Scope((string Id, string Range) dependency, string? trusted = null, bool allowUnsigned = false) =>
        new ProjectManifest
        {
            Dependencies = { [dependency.Id] = dependency.Range },
            ScopedRegistries =
            [
                new ScopedRegistry
                {
                    Name = "test",
                    Url = RegistryPublisher.LocationOf(registry),
                    Scopes = ["user.mateo"],
                    Keys = [trusted ?? registrySigner.PublicKey],
                    AllowUnsigned = allowUnsigned,
                },
            ],
        }.Save(project);

    void Publish(string id, string version, (string Id, string Range)? dependency = null, string? engine = null,
        string? entitlement = null, SshKeygenSigner? publisher = null, string? claim = null, string? into = null)
    {
        var folder = Path.Combine(root, "sources", $"{id}-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "Runtime"));
        var manifest = new PackageManifest { Name = id, Version = SemanticVersion.Parse(version) };
        if (dependency is { } needed)
        {
            manifest.Dependencies[needed.Id] = needed.Range;
        }

        if (engine is not null)
        {
            manifest.Engines["engine"] = VersionRange.Parse(engine);
        }

        if (entitlement is not null)
        {
            manifest.Store = new PackageStoreInfo { Entitlement = entitlement };
        }

        manifest.Save(folder);
        File.WriteAllText(Path.Combine(folder, "Runtime", "Content.txt"), $"{id} {version}");
        var packed = BrickArchive.Pack(folder, Path.Combine(root, "packed", Guid.NewGuid().ToString("N")), ["engine"]);

        RegistryPublisher.Publish(into ?? registry, packed.Path, registrySigner, publisher ?? (claim is null && id.StartsWith("user.mateo", StringComparison.Ordinal) ? mateo : publisher), claim, ["engine"]);
    }

    void Unsign()
    {
        var path = Path.Combine(registry, "v1", "index.json");
        var index = RegistryIndex.Parse(File.ReadAllText(path));
        foreach (var version in index.Bricks.Values.SelectMany(static b => b.Versions.Values))
        {
            version.Signature = null;
            version.SignedBy = null;
        }

        File.WriteAllText(path, index.Serialize());
    }

    SshKeygenSigner Key(string name)
    {
        var path = Path.Combine(root, $"{name}_key");
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-q", "-t", "ed25519", "-N", "", "-C", name, "-f", path })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return new SshKeygenSigner(path);
    }

    static bool KeygenAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ssh-keygen", "-?") { RedirectStandardError = true, RedirectStandardOutput = true });
            process!.WaitForExit();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    sealed class FileHandler(string folder, Action<HttpRequestMessage> observe) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            observe(request);
            var path = Path.Combine(folder, request.RequestUri!.AbsolutePath.TrimStart('/')["v1/".Length..]);
            return Task.FromResult(File.Exists(path)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
