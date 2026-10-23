namespace Gaya.Tests;

/// <summary>Checks registry recovery for declarations of bricks extracted from the host.</summary>
[Collection(SerialTests.Name)]
public sealed class BuiltinRegistryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-builtin-registry-{Guid.NewGuid():N}");
    readonly PackageStore store;
    readonly SshKeygenSigner signer;
    readonly string project;
    readonly string builtins;
    readonly string registry;

    const string Extracted = "com.acme.extracted";
    const string Dependency = "com.acme.dependency";
    const string Shipped = "com.acme.shipped";

    /// <summary>Creates an isolated signed registry and a host with one bundled brick.</summary>
    public BuiltinRegistryTests()
    {
        if (!TestKeys.KeygenAvailable())
        {
            Assert.Skip("ssh-keygen is not installed");
        }

        Directory.CreateDirectory(root);
        project = Path.Combine(root, "project");
        registry = Path.Combine(root, "registry");
        builtins = Path.Combine(root, "builtins");
        store = new PackageStore(Path.Combine(root, "store"));
        signer = TestKeys.Create(root, "registry");
        Directory.CreateDirectory(Path.Combine(builtins, Shipped));
        Manifest(Shipped).Save(Path.Combine(builtins, Shipped));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>Missing built-ins and their extracted dependencies use signed registry versions without manifest edits.</summary>
    [Fact]
    public async Task ExtractedBuiltinsResolveAndRestoreOfflineFromTheirLock()
    {
        Publish(Dependency);
        Publish(Extracted, Dependency);
        var manifest = Project(Registry());
        var original = File.ReadAllText(Path.Combine(project, "Bricks", "manifest.json"));

        var resolution = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(3, resolution.Packages.Count);
        Assert.Equal(PackageOrigin.Builtin, resolution.Packages.Single(p => p.Id == Shipped).Origin);
        Assert.All(resolution.Packages.Where(p => p.Id != Shipped), p =>
        {
            Assert.Equal(PackageOrigin.Registry, p.Origin);
            Assert.Equal(SshSignature.Fingerprint(signer.PublicKey), p.SignedBy);
        });
        Assert.Equal(2, resolution.Warnings.Count);
        Assert.Equal(original, File.ReadAllText(Path.Combine(project, "Bricks", "manifest.json")));
        Assert.Equal($"builtin:{Extracted}", manifest.Dependencies[Extracted]);

        resolution.Lock.Save(project);
        Directory.Delete(registry, recursive: true);
        var offline = await Resolver(locked: true).ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal(resolution.Lock.Serialize(), offline.Lock.Serialize());
    }

    /// <summary>A still-bundled brick wins even when a registry offers a newer version.</summary>
    [Fact]
    public async Task ExistingBuiltinsKeepTheirBundledSource()
    {
        Publish(Shipped);
        var bundled = Manifest(Shipped);
        bundled.Version = SemanticVersion.Parse("0.5.0");
        bundled.Save(Path.Combine(builtins, Shipped));
        new ProjectManifest
        {
            Dependencies = { [Shipped] = $"builtin:{Shipped}" },
            ScopedRegistries = [Registry()],
        }.Save(project);

        var result = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(PackageOrigin.Builtin, Assert.Single(result.Packages).Origin);
        Assert.Empty(result.Warnings);
        Assert.False(Directory.Exists(Path.Combine(store.Root, Shipped)));
    }

    /// <summary>Absent registries, unrelated scopes and mismatched builtin ids remain explicit failures.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("scope")]
    [InlineData("mismatch")]
    public async Task InvalidBuiltinDeclarationsDoNotUseAnUnrelatedRegistry(string scenario)
    {
        Publish(Extracted);
        var registryEntry = Registry();
        var manifest = Project(registryEntry);
        if (scenario == "none")
        {
            manifest.ScopedRegistries.Clear();
        }
        else if (scenario == "scope")
        {
            registryEntry.Scopes = ["com.other"];
        }
        else
        {
            manifest.Dependencies[Extracted] = "builtin:com.acme.someone-else";
        }

        manifest.Save(project);
        await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));
    }

    /// <summary>Registry replacements obey the same trust checks as ordinary registry dependencies.</summary>
    [Fact]
    public async Task ExtractedBuiltinsRequireTrustedRegistrySignatures()
    {
        Publish(Extracted);
        var registryEntry = Registry();
        registryEntry.Keys = [TestKeys.Create(root, "untrusted").PublicKey];
        Project(registryEntry);

        var error = await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));

        Assert.Contains("key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Locked restore never silently replaces a previously locked built-in with a registry version.</summary>
    [Fact]
    public async Task OldBuiltinLocksRequireAnUnlockedRestore()
    {
        Publish(Extracted);
        Project(Registry());
        new LockFile
        {
            Dependencies =
            {
                [Extracted] = new LockEntry { Version = SemanticVersion.Parse("1.0.0"), Source = $"builtin:{Extracted}", Depth = 1 },
                [Shipped] = new LockEntry { Version = SemanticVersion.Parse("1.0.0"), Source = $"builtin:{Shipped}", Depth = 1 },
            },
        }.Save(project);
        var before = File.ReadAllText(Path.Combine(project, "Bricks", LockFile.FileName));

        var error = await Assert.ThrowsAsync<PackageException>(() => Resolver(locked: true).ResolveAsync(project, TestContext.Current.CancellationToken));

        Assert.Contains("resolve without --locked", error.Message);
        Assert.Equal(before, File.ReadAllText(Path.Combine(project, "Bricks", LockFile.FileName)));
    }

    /// <summary>Saving a registry and explicitly refreshing both recover failed workspaces and apply restored bricks.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistryRefreshRecoversExtractedBuiltins(bool addRegistry)
    {
        var manifest = Project();
        manifest.Dependencies[Dependency] = $"builtin:{Dependency}";
        manifest.Save(project);
        var workspace = new StudioBricks(project, new Dictionary<string, SemanticVersion>(), store, builtinDirectory: builtins);
        var applied = 0;
        workspace.Applied += changed => applied += changed ? 1 : 0;
        var controller = new BricksController(workspace, null) { Scope = BrickScope.Studio, Store = store };
        controller.Refresh();
        Assert.NotNull(controller.Error);
        Assert.Empty(controller.Rows);

        Publish(Dependency);
        Publish(Extracted, Dependency);
        if (addRegistry)
        {
            Assert.True(await controller.SaveRegistryAsync(null, Registry()));
        }
        else
        {
            workspace.AddRegistry(Registry());
            await controller.RefreshRegistriesAsync();
        }

        Assert.Null(controller.Error);
        Assert.Equal(3, controller.Rows.Count);
        Assert.Equal(PackageOrigin.Builtin, controller.Rows.Single(row => row.Id == Shipped).Origin);
        Assert.Equal(PackageOrigin.Registry, controller.Rows.Single(row => row.Id == Extracted).Origin);
        Assert.Contains(controller.Catalog, brick => brick.Id == Shipped && brick.IsBuiltin);
        Assert.True(applied > 0);
        Assert.Equal($"builtin:{Extracted}", ProjectManifest.Load(project).Manifest.Dependencies[Extracted]);
    }

    /// <summary>Refreshing a registry that lacks the replacement keeps the workspace error visible.</summary>
    [Fact]
    public async Task RegistryRefreshKeepsUnresolvedBuiltinErrorsVisible()
    {
        Publish(Shipped);
        Project(Registry());
        var workspace = new StudioBricks(project, new Dictionary<string, SemanticVersion>(), store, builtinDirectory: builtins);
        var controller = new BricksController(workspace, null) { Scope = BrickScope.Studio, Store = store };
        controller.Refresh();
        Assert.NotNull(controller.Error);

        await controller.RefreshRegistriesAsync();

        Assert.NotNull(controller.Error);
        Assert.Contains(Extracted, controller.Error);
        Assert.Empty(controller.Rows);
        Assert.Contains(controller.Catalog, static brick => brick.Id == Shipped && brick.IsBuiltin);
    }

    PackageResolver Resolver(bool locked = false) => new(store, new PackageResolverOptions
    {
        BuiltinDirectory = builtins,
        Locked = locked,
    });

    ProjectManifest Project(params ScopedRegistry[] registries)
    {
        var manifest = new ProjectManifest
        {
            Dependencies = { [Extracted] = $"builtin:{Extracted}", [Shipped] = $"builtin:{Shipped}" },
            ScopedRegistries = [.. registries],
        };
        manifest.Save(project);
        return manifest;
    }

    ScopedRegistry Registry() => new()
    {
        Name = "test",
        Url = RegistryPublisher.LocationOf(registry),
        Keys = [signer.PublicKey],
    };

    static PackageManifest Manifest(string id) => new()
    {
        Name = id,
        Version = SemanticVersion.Parse("1.0.0"),
        Scopes = [PackageScope.Project, PackageScope.Studio],
    };

    void Publish(string id, string? dependency = null)
    {
        var folder = Path.Combine(root, "sources", id);
        Directory.CreateDirectory(folder);
        var manifest = Manifest(id);
        if (dependency is not null)
        {
            manifest.Dependencies[dependency] = $"builtin:{dependency}";
        }

        manifest.Save(folder);
        var packed = BrickArchive.Pack(folder, Path.Combine(root, "packed"));
        RegistryPublisher.Publish(registry, packed.Path, signer, signer, claim: "com.acme");
    }
}
