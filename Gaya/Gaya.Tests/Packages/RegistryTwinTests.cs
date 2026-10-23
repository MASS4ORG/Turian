namespace Gaya.Tests;

/// <summary>
/// Projects that take bake twins: a registry brick resolves to its twin only when the twin is signed, published by the
/// source's publisher or a baker it trusts, and baked from the exact source file the registry serves.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class RegistryTwinTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-twins-{Guid.NewGuid():N}");
    readonly string registry;
    readonly string project;
    readonly PackageStore store;
    readonly SshKeygenSigner registrySigner;
    readonly SshKeygenSigner mateo;
    readonly SshKeygenSigner baker;

    /// <summary>Creates keys, an empty registry and a project taking twins from it.</summary>
    public RegistryTwinTests()
    {
        Directory.CreateDirectory(root);
        if (!TestKeys.KeygenAvailable())
        {
            Assert.Skip("ssh-keygen is not installed");
        }

        registry = Path.Combine(root, "registry");
        project = Path.Combine(root, "game");
        Directory.CreateDirectory(project);
        store = new PackageStore(Path.Combine(root, "store"));
        registrySigner = TestKeys.Create(root, "registry");
        mateo = TestKeys.Create(root, "mateo");
        baker = TestKeys.Create(root, "baker");
        new ProjectManifest
        {
            Dependencies = { ["user.mateo.shop"] = "^1.0.0" },
            ScopedRegistries = [new ScopedRegistry { Name = "test", Url = RegistryPublisher.LocationOf(registry), Keys = [registrySigner.PublicKey] }],
            Twins = true,
        }.Save(project);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// With twins on, every brick with a published twin resolves to it, the lock pins the source it stands in for, and
    /// a locked install needs no registry; a brick listed in sources stays source.
    /// </summary>
    [Fact]
    public async Task TwinsReplaceRegistryBricksAndAreLocked()
    {
        var source = Publish("user.mateo.rules", "1.1.0", mateo);
        PublishTwin("user.mateo.rules", "1.1.0", mateo);
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));

        var resolution = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(["user.mateo.rules.bake", "user.mateo.shop"], resolution.Packages.Select(static p => p.Id));
        Assert.Empty(resolution.Warnings);
        var bakeOf = resolution.Lock.Dependencies["user.mateo.rules.bake"].BakeOf!;
        Assert.Equal(("user.mateo.rules", source), (bakeOf.Brick, bakeOf.Integrity));
        var link = Assert.Single(Index().Bricks["user.mateo.rules"].Versions["1.1.0"].Bakes!);
        Assert.Equal(("user.mateo.rules.bake", "1.1.0"), (link.Brick, link.Version));

        resolution.Lock.Save(project);
        Directory.Delete(registry, recursive: true);
        var offline = await Resolver(locked: true).ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal(["user.mateo.rules.bake", "user.mateo.shop"], offline.Packages.Select(static p => p.Id));
    }

    /// <summary>Listing a brick in sources, even only in the per-user override, takes the source and keeps the lock.</summary>
    [Fact]
    public async Task SourcesKeepABrickAsSource()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishTwin("user.mateo.rules", "1.1.0", mateo);
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        File.WriteAllText(Path.Combine(project, ProjectManifest.DirectoryName, ProjectManifest.UserFileName),
            """{ "sources": ["user.mateo.rules"] }""");

        var resolution = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(["user.mateo.rules", "user.mateo.shop"], resolution.Packages.Select(static p => p.Id));
        Assert.True(resolution.UsesUserOverride);
    }

    /// <summary>
    /// A twin from another publisher resolves only when the source names its key among its trusted bakers; otherwise
    /// the source is used, with a warning.
    /// </summary>
    [Fact]
    public async Task OnlyTrustedBakersTwinsResolve()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishTwin("user.mateo.rules", "1.1.0", baker, id: "user.baker.rules");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));

        var untrusted = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(["user.mateo.rules", "user.mateo.shop"], untrusted.Packages.Select(static p => p.Id));
        Assert.Contains("nor a baker it trusts", Assert.Single(untrusted.Warnings), StringComparison.Ordinal);

        Publish("user.mateo.rules", "1.2.0", mateo, trustedBaker: SshSignature.Fingerprint(baker.PublicKey));
        PublishTwin("user.mateo.rules", "1.2.0", baker, id: "user.baker.rules");
        var trusted = await Resolver(updateAll: true).ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(["user.baker.rules", "user.mateo.shop"], trusted.Packages.Select(static p => p.Id));
        Assert.Empty(trusted.Warnings);
    }

    /// <summary>
    /// A twin baked from another file than the registry serves is refused: asked for by its own id it fails, taken in
    /// place of the source it falls back to the source.
    /// </summary>
    [Fact]
    public async Task TwinsOfAnotherSourceFileAreRefused()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishTwin("user.mateo.rules", "1.1.0", mateo, integrity: "sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));

        var fallback = await Resolver().ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal(["user.mateo.rules", "user.mateo.shop"], fallback.Packages.Select(static p => p.Id));
        Assert.Contains("another user.mateo.rules 1.1.0 file", Assert.Single(fallback.Warnings), StringComparison.Ordinal);

        var manifest = ProjectManifest.Load(project).Manifest;
        manifest.Twins = false;
        manifest.Dependencies["user.mateo.rules.bake"] = "^1.0.0";
        manifest.Save(project);
        var refused = await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));
        Assert.Contains("another user.mateo.rules 1.1.0 file", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>An explicit twin cannot bypass source verification when its source is absent from the registry.</summary>
    [Fact]
    public async Task TwinsWithAnUnavailableSourceAreRefused()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishTwin("user.mateo.rules", "1.1.0", mateo);
        var index = Index();
        index.Bricks.Remove("user.mateo.rules");
        File.WriteAllText(Path.Combine(RegistryPublisher.LocationOf(registry), "index.json"), index.Serialize());
        var manifest = ProjectManifest.Load(project).Manifest;
        manifest.Dependencies.Clear();
        manifest.Dependencies["user.mateo.rules.bake"] = "1.1.0";
        manifest.Save(project);

        var error = await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(project, TestContext.Current.CancellationToken));

        Assert.Contains("its source cannot be verified", error.Message, StringComparison.Ordinal);
    }

    PackageResolver Resolver(bool locked = false, bool updateAll = false) => new(store, new PackageResolverOptions
    {
        ReservedCategoryPrefixes = ["engine"],
        Locked = locked,
        Update = updateAll ? null : new HashSet<string>(),
    });

    RegistryIndex Index() => RegistryIndex.Parse(File.ReadAllText(Path.Combine(RegistryPublisher.LocationOf(registry), "index.json")));

    /// <summary>Publishes a source brick and returns its integrity.</summary>
    string Publish(string id, string version, SshKeygenSigner publisher, (string Id, string Range)? dependency = null,
        string? trustedBaker = null)
    {
        var manifest = new PackageManifest { Name = id, Version = SemanticVersion.Parse(version) };
        if (dependency is { } needed)
        {
            manifest.Dependencies[needed.Id] = needed.Range;
        }

        if (trustedBaker is not null)
        {
            manifest.TrustedBakers.Add(trustedBaker);
        }

        var packed = Pack(manifest);
        RegistryPublisher.Publish(registry, packed.Path, registrySigner, publisher, reservedCategoryPrefixes: ["engine"]);
        return packed.Integrity;
    }

    /// <summary>Publishes a twin of the source version the registry serves, or of a file with another integrity.</summary>
    void PublishTwin(string sourceId, string version, SshKeygenSigner publisher, string? id = null, string? integrity = null)
    {
        var manifest = new PackageManifest
        {
            Name = id ?? BrickArchive.TwinIdOf(sourceId),
            Version = SemanticVersion.Parse(version),
            BakeOf = new PackageBakeOf
            {
                Brick = sourceId,
                Version = SemanticVersion.Parse(version),
                Integrity = integrity ?? Index().Bricks[sourceId].Versions[version].Integrity,
            },
        };

        RegistryPublisher.Publish(registry, Pack(manifest).Path, registrySigner, publisher, reservedCategoryPrefixes: ["engine"]);
    }

    BrickPackResult Pack(PackageManifest manifest)
    {
        var folder = Path.Combine(root, "sources", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Runtime"));
        manifest.Save(folder);
        File.WriteAllText(Path.Combine(folder, "Runtime", "Content.txt"), $"{manifest.Name} {manifest.Version}");
        return BrickArchive.Pack(folder, Path.Combine(root, "packed", Guid.NewGuid().ToString("N")), ["engine"]);
    }
}
