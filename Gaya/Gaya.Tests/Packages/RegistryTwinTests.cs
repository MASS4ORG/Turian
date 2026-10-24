using System.Net;

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

    /// <summary>
    /// A bake published as a variant keeps the source's id: the host takes its own major and minor, else an older
    /// minor of its major, and locks the variant; another major takes the source.
    /// </summary>
    [Fact]
    public async Task VariantsAreChosenForThisHostAndLocked()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.1");
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2");
        PublishVariant("user.mateo.rules", "1.1.0", "engine2.0");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));

        var exact = await Resolver(host: "1.2.5").ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal(["user.mateo.rules", "user.mateo.shop"], exact.Packages.Select(static p => p.Id));
        Assert.Equal("engine1.2", exact.Packages[0].Manifest.Variant);
        Assert.Empty(exact.Warnings);
        var locked = exact.Lock.Dependencies["user.mateo.rules"];
        Assert.Equal(("engine1.2", "user.mateo.rules"), (locked.Variant, locked.BakeOf!.Brick));
        Assert.Equal(["engine1.1", "engine1.2", "engine2.0"], Index().Bricks["user.mateo.rules"].Versions["1.1.0"].Variants!.Keys);

        Assert.Equal("engine1.2", (await Resolver(host: "1.4.0").ResolveAsync(project, TestContext.Current.CancellationToken)).Packages[0].Manifest.Variant);
        Assert.Equal("engine1.1", (await Resolver(host: "1.1.0").ResolveAsync(project, TestContext.Current.CancellationToken)).Packages[0].Manifest.Variant);
        var otherMajor = await Resolver(host: "3.0.0").ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Null(otherMajor.Packages[0].Manifest.BakeOf);

        // A locked older variant is downloaded again as locked, over the newer one this host would choose.
        (await Resolver(host: "1.1.0").ResolveAsync(project, TestContext.Current.CancellationToken)).Lock.Save(project);
        Directory.Delete(Path.Combine(root, "store"), recursive: true);
        var relocked = await Resolver(host: "1.2.5").ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("engine1.1", relocked.Packages[0].Manifest.Variant);

        File.Delete(Path.Combine(project, ProjectManifest.DirectoryName, LockFile.FileName));
        (await Resolver(host: "1.2.5").ResolveAsync(project, TestContext.Current.CancellationToken)).Lock.Save(project);
        Directory.Delete(registry, recursive: true);
        var offline = await Resolver(locked: true, host: "1.2.5").ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("engine1.2", offline.Packages[0].Manifest.Variant);
    }

    /// <summary>
    /// A static registry takes a bake only after the exact source file it names, as a variant of that entry, and
    /// never twice; the source entry itself stays.
    /// </summary>
    [Fact]
    public void BakesNeedTheirSourceAndAreImmutable()
    {
        var bakeOf = new PackageBakeOf { Brick = "user.mateo.rules", Version = SemanticVersion.Parse("1.1.0"), Integrity = "sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" };
        var orphan = Pack(new PackageManifest { Name = "user.mateo.rules", Version = SemanticVersion.Parse("1.1.0") }, bakeOf: bakeOf, variant: "engine1.2");
        Assert.Contains("publish that source first", Assert.Throws<PackageException>(() =>
            RegistryPublisher.Publish(registry, orphan.Path, registrySigner, mateo, reservedCategoryPrefixes: ["engine"])).Message, StringComparison.Ordinal);

        var source = Publish("user.mateo.rules", "1.1.0", mateo);
        Assert.Contains("publish that source first", Assert.Throws<PackageException>(() =>
            RegistryPublisher.Publish(registry, orphan.Path, registrySigner, mateo, reservedCategoryPrefixes: ["engine"])).Message, StringComparison.Ordinal);

        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2");
        Assert.Contains("already published", Assert.Throws<PackageException>(() =>
            PublishVariant("user.mateo.rules", "1.1.0", "engine1.2")).Message, StringComparison.Ordinal);
        var entry = Index().Bricks["user.mateo.rules"].Versions["1.1.0"];
        Assert.Equal(source, entry.Integrity);
        Assert.Equal(("user.mateo.rules", "1.1.0", "engine1.2"),
            (entry.Variants!["engine1.2"].PackageId, entry.Variants["engine1.2"].Version, entry.Variants["engine1.2"].Variant));
    }

    /// <summary>A locked bake is never taken for its source once the project keeps the brick as source.</summary>
    [Fact]
    public async Task LockedVariantsAreNotTakenForTheSource()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        (await Resolver(host: "1.2.0").ResolveAsync(project, TestContext.Current.CancellationToken)).Lock.Save(project);
        var manifest = ProjectManifest.Load(project).Manifest;
        manifest.Sources.Add("user.mateo.rules");
        manifest.Save(project);

        var resolution = await Resolver(host: "1.2.0").ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Null(resolution.Packages[0].Manifest.BakeOf);
        Assert.Null(resolution.Lock.Dependencies["user.mateo.rules"].Variant);
    }

    /// <summary>A bake whose precast this host cannot load falls back to its source, with a warning.</summary>
    [Fact]
    public async Task UnloadableVariantsFallBackToTheSource()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2",
            new PackagePrecast { BuiltWith = { ["engine"] = SemanticVersion.Parse("1.1.0") } });
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));

        var resolution = await Resolver(host: "1.2.0").ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Null(resolution.Packages[0].Manifest.BakeOf);
        Assert.Contains("which engine 1.2.0 does not load", Assert.Single(resolution.Warnings), StringComparison.Ordinal);
    }

    /// <summary>
    /// A store's curator advisory refusing the build of a bake leaves the source, linking the record; a warning-only
    /// one keeps the bake and is reported. Without a recorded build, the bake's precast version is matched.
    /// </summary>
    [Fact]
    public async Task StoreAdvisoriesRefuseOrWarnAboutBakes()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine3.3",
            new PackagePrecast { BuiltWith = { ["engine"] = SemanticVersion.Parse("3.3.0") } });
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        var http = new RegistryFolderHandler(RegistryPublisher.LocationOf(registry));
        var manifest = ProjectManifest.Load(project).Manifest;
        manifest.ScopedRegistries[0].Url = "https://store.test/v1";
        manifest.Save(project);

        http.Advisories = """[ { "id": "11111111-1111-1111-1111-111111111111", "packagePrefix": "user.mateo", "turianVersionRange": ">=3.3.0 <3.4.0", "message": "Broken bakes.", "reject": true } ]""";
        var refused = await Resolver(host: "3.3.1", buildHost: true, http: http).ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Null(refused.Packages[0].Manifest.BakeOf);
        Assert.Contains("https://store.test/api/v1/build-advisories/11111111-1111-1111-1111-111111111111", Assert.Single(refused.Warnings), StringComparison.Ordinal);

        http.Advisories = """[ { "id": "22222222-2222-2222-2222-222222222222", "packagePrefix": "user.mateo.rules", "turianVersionRange": ">=3.0.0", "message": "Rebake soon.", "reject": false } ]""";
        var warned = await Resolver(host: "3.3.1", buildHost: true, http: http).ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("engine3.3", warned.Packages[0].Manifest.Variant);
        Assert.Contains("Rebake soon.", Assert.Single(warned.Warnings), StringComparison.Ordinal);

        // A host that records no build version for itself matches no precast version, so the range advisory is moot.
        var unmatched = await Resolver(host: "3.3.1", http: http).ResolveAsync(project, TestContext.Current.CancellationToken);
        Assert.Empty(unmatched.Warnings);
    }

    /// <summary>A bake the store recorded as built by another major of the build host is left for its source.</summary>
    [Fact]
    public async Task BakesBuiltByAnotherMajorFallBackToTheSource()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        var index = Index();
        index.Bricks["user.mateo.rules"].Versions["1.1.0"].Variants!["engine1.2"].Build =
            new RegistryBuild($"ghcr.io/example/cli@sha256:{new string('a', 64)}", "2.0.0");
        File.WriteAllText(Path.Combine(RegistryPublisher.LocationOf(registry), "index.json"), index.Serialize());

        var resolution = await Resolver(host: "1.2.0", buildHost: true).ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Null(resolution.Packages[0].Manifest.BakeOf);
        Assert.Contains("another major version than 1.2.0", Assert.Single(resolution.Warnings), StringComparison.Ordinal);
    }

    /// <summary>A locked variant from another host major takes its source, with or without a cached bake.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedVariantsFromAnotherMajorFallBackToSource(bool cached)
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        (await Resolver(host: "1.2.0").ResolveAsync(project, TestContext.Current.CancellationToken)).Lock.Save(project);
        Publish("user.mateo.rules", "1.2.0", mateo);
        if (!cached)
        {
            Directory.Delete(Path.Combine(root, "store"), recursive: true);
        }

        var resolution = await Resolver(host: "3.0.0").ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Null(resolution.Packages[0].Manifest.BakeOf);
        Assert.Equal("1.1.0", resolution.Packages[0].Version.ToString());
        Assert.Contains("Using user.mateo.rules 1.1.0 as source", Assert.Single(resolution.Warnings), StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<PackageException>(() => Resolver(locked: true, host: "3.0.0")
            .ResolveAsync(project, TestContext.Current.CancellationToken));
        Assert.Contains("resolve without --locked", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Cached locked bakes still consult recorded build advisories, including warning-only records.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedBakesConsultBuildAdvisories(bool reject)
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        var image = $"ghcr.io/example/cli@sha256:{new string('a', 64)}";
        var index = Index();
        index.Bricks["user.mateo.rules"].Versions["1.1.0"].Variants!["engine1.2"].Build = new RegistryBuild(image, "1.2.0");
        File.WriteAllText(Path.Combine(RegistryPublisher.LocationOf(registry), "index.json"), index.Serialize());
        var http = new RegistryFolderHandler(RegistryPublisher.LocationOf(registry));
        var manifest = ProjectManifest.Load(project).Manifest;
        manifest.ScopedRegistries[0].Url = "https://store.test/v1";
        manifest.Save(project);
        (await Resolver(host: "1.2.0", http: http).ResolveAsync(project, TestContext.Current.CancellationToken)).Lock.Save(project);
        http.Advisories = $$"""[ { "id": "11111111-1111-1111-1111-111111111111", "packagePrefix": "user.mateo", "image": "{{image}}", "turianVersionRange": ">=1.2.0 <1.3.0", "message": "Packer advisory.", "reject": {{reject.ToString().ToLowerInvariant()}} } ]""";

        var resolution = await Resolver(locked: !reject, host: "1.2.0", http: http)
            .ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(reject, resolution.Packages[0].Manifest.BakeOf is null);
        Assert.Contains("https://store.test/api/v1/build-advisories/11111111-1111-1111-1111-111111111111",
            Assert.Single(resolution.Warnings), StringComparison.Ordinal);
    }

    /// <summary>A variant entry naming no signed identity of its own is verified under its source's id and version.</summary>
    [Fact]
    public async Task VariantsWithoutTheirOwnIdentityUseTheSources()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        var index = Index();
        var variant = index.Bricks["user.mateo.rules"].Versions["1.1.0"].Variants!["engine1.2"];
        (variant.PackageId, variant.Version) = (null, null);
        File.WriteAllText(Path.Combine(RegistryPublisher.LocationOf(registry), "index.json"), index.Serialize());

        var resolution = await Resolver(host: "1.2.0").ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal("engine1.2", resolution.Packages[0].Manifest.Variant);
        Assert.Equal("1.1.0", resolution.Packages[0].Version.ToString());
        Assert.Empty(resolution.Warnings);
    }

    /// <summary>A store whose advisories cannot be read counts as advising nothing, so its bake is kept.</summary>
    [Fact]
    public async Task UnreadableBuildAdvisoriesKeepTheBake()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        var http = new RegistryFolderHandler(RegistryPublisher.LocationOf(registry))
        {
            AdvisoriesStatus = HttpStatusCode.InternalServerError,
        };
        var manifest = ProjectManifest.Load(project).Manifest;
        manifest.ScopedRegistries[0].Url = "https://store.test/v1";
        manifest.Save(project);

        var resolution = await Resolver(host: "1.2.0", http: http).ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.NotNull(resolution.Packages[0].Manifest.BakeOf);
        Assert.Empty(resolution.Warnings);
    }

    /// <summary>Malformed advisories are a store defect that stops resolution and names the endpoint, never "no advisories".</summary>
    [Fact]
    public async Task MalformedBuildAdvisoriesStopResolution()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        var http = new RegistryFolderHandler(RegistryPublisher.LocationOf(registry)) { Advisories = "[{" };
        var manifest = ProjectManifest.Load(project).Manifest;
        manifest.ScopedRegistries[0].Url = "https://store.test/v1";
        manifest.Save(project);

        var error = await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => Resolver(host: "1.2.0", http: http)
            .ResolveAsync(project, TestContext.Current.CancellationToken));

        Assert.Contains("store.test answered GET /api/v1/build-advisories with malformed JSON, a store defect",
            error.Message, StringComparison.Ordinal);
    }

    /// <summary>Cached precast assemblies are checked against the running host before reuse.</summary>
    [Fact]
    public async Task CachedPrecastFromAnotherMinorFallsBackToSource()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2",
            new PackagePrecast { BuiltWith = { ["engine"] = SemanticVersion.Parse("1.2.0") } });
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        (await Resolver(host: "1.2.0").ResolveAsync(project, TestContext.Current.CancellationToken)).Lock.Save(project);

        var resolution = await Resolver(host: "1.3.0").ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Null(resolution.Packages[0].Manifest.BakeOf);
        Assert.Contains("which engine 1.3.0 does not load", Assert.Single(resolution.Warnings), StringComparison.Ordinal);
    }

    /// <summary>A bake with an invalid registry signature is refused before source fallback.</summary>
    [Fact]
    public async Task InvalidBakeSignaturesFallBackToSource()
    {
        Publish("user.mateo.rules", "1.1.0", mateo);
        PublishVariant("user.mateo.rules", "1.1.0", "engine1.2");
        Publish("user.mateo.shop", "1.0.0", mateo, dependency: ("user.mateo.rules", "^1.0.0"));
        var index = Index();
        index.Bricks["user.mateo.rules"].Versions["1.1.0"].Variants!["engine1.2"].Signature = "invalid";
        File.WriteAllText(Path.Combine(RegistryPublisher.LocationOf(registry), "index.json"), index.Serialize());

        var resolution = await Resolver(host: "1.2.0").ResolveAsync(project, TestContext.Current.CancellationToken);

        Assert.Null(resolution.Packages[0].Manifest.BakeOf);
        Assert.Contains("Using user.mateo.rules 1.1.0 as source", Assert.Single(resolution.Warnings), StringComparison.Ordinal);
    }

    PackageResolver Resolver(bool locked = false, bool updateAll = false, string? host = null, bool buildHost = false,
        HttpMessageHandler? http = null) => new(store, new PackageResolverOptions
        {
            ReservedCategoryPrefixes = ["engine"],
            Locked = locked,
            Update = updateAll ? null : new HashSet<string>(),
            Hosts = host is not null ? new Dictionary<string, SemanticVersion> { ["engine"] = SemanticVersion.Parse(host) }
                : new Dictionary<string, SemanticVersion>(),
            BuildHost = buildHost ? "engine" : null,
            Http = http is null ? null : new HttpClient(http),
        });

    /// <summary>Publishes a bake of the source version the registry serves as a variant with the source's id.</summary>
    void PublishVariant(string id, string version, string variant, PackagePrecast? precast = null)
    {
        var bakeOf = new PackageBakeOf { Brick = id, Version = SemanticVersion.Parse(version), Integrity = Index().Bricks[id].Versions[version].Integrity };
        var packed = Pack(new PackageManifest { Name = id, Version = SemanticVersion.Parse(version) }, precast, bakeOf, variant);
        RegistryPublisher.Publish(registry, packed.Path, registrySigner, mateo, reservedCategoryPrefixes: ["engine"]);
    }

    /// <summary>Serves a static registry folder over https at <c>store.test/v1</c>, with a store's advisories beside it.</summary>
    sealed class RegistryFolderHandler(string location) : HttpMessageHandler
    {
        public string Advisories { get; set; } = "[]";

        public HttpStatusCode AdvisoriesStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var file = path.StartsWith("/v1/", StringComparison.Ordinal) ? Path.Combine(location, path["/v1/".Length..]) : null;
            return Task.FromResult(path == "/api/v1/build-advisories"
                ? new HttpResponseMessage(AdvisoriesStatus) { Content = new StringContent(Advisories) }
                : file is not null && File.Exists(file)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(file)) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

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

    BrickPackResult Pack(PackageManifest manifest, PackagePrecast? precast = null, PackageBakeOf? bakeOf = null, string? variant = null)
    {
        var folder = Path.Combine(root, "sources", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Runtime"));
        manifest.Save(folder);
        File.WriteAllText(Path.Combine(folder, "Runtime", "Content.txt"), $"{manifest.Name} {manifest.Version}");
        return BrickArchive.Pack(folder, Path.Combine(root, "packed", Guid.NewGuid().ToString("N")), ["engine"], precast,
            bakeOf: bakeOf, variant: variant);
    }
}
