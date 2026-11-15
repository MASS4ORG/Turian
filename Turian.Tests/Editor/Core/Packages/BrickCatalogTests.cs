namespace Turian.Tests;

/// <summary>The catalog that merges installed, built-in, stored and registry bricks into available, installed and enabled.</summary>
public sealed class BrickCatalogTests : IDisposable
{
    static readonly string[] Reserved = ["gaya", ProjectPackages.HostName];

    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-brick-catalog-{Guid.NewGuid():N}");

    /// <summary>Creates the scratch folder.</summary>
    public BrickCatalogTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>What the project uses is enabled, what sits in the store is installed, and the rest is available.</summary>
    [Fact]
    public void StatesFollowAvailableInstalledEnabled()
    {
        var builtins = Path.Combine(root, "builtins");
        _ = BrickService.New(builtins, "user.mateo.builtin");
        var store = new PackageStore(Path.Combine(root, "store"));
        Directory.CreateDirectory(store.Root);
        var stored = Path.Combine(store.Root, "user.mateo.aside@abc");
        Directory.Move(BrickService.New(root, "user.mateo.aside"), stored);
        store.RecordOrigin(stored, "git+https://example.com/aside.git");
        var used = Resolved(BrickService.New(root, "user.mateo.used"));

        var catalog = BrickCatalog.Local([used], builtins, store, Reserved).ToDictionary(static b => b.Id);

        Assert.Equal(BrickState.Enabled, catalog["user.mateo.used"].State);
        Assert.Equal(BrickState.Installed, catalog["user.mateo.aside"].State);
        Assert.Equal("0.1.0", catalog["user.mateo.aside"].InstalledVersion);
        Assert.Equal(BrickState.Available, catalog["user.mateo.builtin"].State);
        Assert.True(catalog["user.mateo.builtin"].IsBuiltin);
        Assert.Null(catalog["user.mateo.builtin"].InstallSource);
    }

    /// <summary>A stored brick is available from the origin the client recorded, never from what the brick says.</summary>
    [Fact]
    public void StoredBricksUseTheRecordedOrigin()
    {
        var store = new PackageStore(Path.Combine(root, "store"));
        Directory.CreateDirectory(store.Root);
        var folder = Path.Combine(store.Root, "user.mateo.stored@abc");
        Directory.Move(BrickService.New(root, "user.mateo.stored"), folder);
        var unknown = Path.Combine(store.Root, "user.mateo.unknown@abc");
        Directory.Move(BrickService.New(root, "user.mateo.unknown"), unknown);
        store.RecordOrigin(folder, "git+https://example.com/stored.git#v1");

        var catalog = BrickCatalog.Local([], null, store, Reserved).ToDictionary(static b => b.Id);

        Assert.Equal("git+https://example.com/stored.git#v1", catalog["user.mateo.stored"].Origin);
        Assert.Equal("git+https://example.com/stored.git#v1", catalog["user.mateo.stored"].InstallSource);
        Assert.Equal(BrickState.Installed, catalog["user.mateo.unknown"].State);
        Assert.Null(catalog["user.mateo.unknown"].InstallSource);
    }

    /// <summary>A brick stored from a registry is enabled by version range, and a registry listing fills in an unrecorded origin.</summary>
    [Fact]
    public void RegistryBricksAreEnabledByRange()
    {
        var store = new PackageStore(Path.Combine(root, "store"));
        Directory.CreateDirectory(store.Root);
        var fromRegistry = Path.Combine(store.Root, "user.mateo.fetched@sha256-1");
        Directory.Move(BrickService.New(root, "user.mateo.fetched"), fromRegistry);
        store.RecordOrigin(fromRegistry, "registry:bricks.example");
        var unrecorded = Path.Combine(store.Root, "user.mateo.old@sha256-2");
        Directory.Move(BrickService.New(root, "user.mateo.old"), unrecorded);

        var catalog = BrickCatalog.WithRegistries(BrickCatalog.Local([], null, store, Reserved),
            [Listing("user.mateo.old", "0.1.0")]).ToDictionary(static b => b.Id);

        Assert.Equal("^0.1.0", catalog["user.mateo.fetched"].InstallSource);
        Assert.Equal("^0.1.0", catalog["user.mateo.old"].InstallSource);
        Assert.Equal("registry:bricks.example", catalog["user.mateo.old"].Origin);
    }

    /// <summary>A registry brick the project lacks is available; a newer one marks an installed brick as updatable.</summary>
    [Fact]
    public void RegistriesAddAvailableBricksAndUpdates()
    {
        var used = Resolved(BrickService.New(root, "user.mateo.used"));
        var local = BrickCatalog.Local([used], null, null, Reserved);

        var catalog = BrickCatalog.WithRegistries(local, [Listing("user.mateo.used", "9.0.0"), Listing("user.mateo.remote", "1.2.0")])
            .ToDictionary(static b => b.Id);

        Assert.True(catalog["user.mateo.used"].HasUpdate);
        Assert.Equal(BrickState.Available, catalog["user.mateo.remote"].State);
        Assert.Equal("^1.2.0", catalog["user.mateo.remote"].InstallSource);
        Assert.Equal("registry:bricks.example", catalog["user.mateo.remote"].Origin);
    }

    /// <summary>Filters and search narrow the list.</summary>
    [Fact]
    public void FilterAndSearchNarrowTheList()
    {
        var builtins = Path.Combine(root, "builtins");
        _ = BrickService.New(builtins, "user.mateo.builtin");
        var used = Resolved(BrickService.New(root, "user.mateo.used"));
        var catalog = BrickCatalog.Local([used], builtins, null, Reserved);

        Assert.Equal(["user.mateo.used"], BrickCatalog.Filter(catalog, BrickFilter.Installed, null).Select(static b => b.Id));
        Assert.Equal(["user.mateo.builtin"], BrickCatalog.Filter(catalog, BrickFilter.Available, null).Select(static b => b.Id));
        Assert.Equal(["user.mateo.builtin"], BrickCatalog.Filter(catalog, BrickFilter.All, "BUILTIN").Select(static b => b.Id));
        Assert.Empty(BrickCatalog.Filter(catalog, BrickFilter.Updates, null));
    }

    /// <summary>Search finds a brick by what its description says, not only by its id, name or author.</summary>
    [Fact]
    public void SearchMatchesTheDescription()
    {
        var builtins = Path.Combine(root, "builtins");
        var folder = BrickService.New(builtins, "user.mateo.mixer");
        new PackageManifest { Name = "user.mateo.mixer", Version = SemanticVersion.Parse("1.0.0"), Description = "Plays Audio clips" }
            .Save(folder);
        _ = BrickService.New(builtins, "user.mateo.other");
        var catalog = BrickCatalog.Local([], builtins, null, Reserved);

        Assert.Equal(["user.mateo.mixer"], BrickCatalog.Filter(catalog, BrickFilter.All, "audio").Select(static b => b.Id));
    }

    /// <summary>A brick that is only downloaded is neither installed nor updatable, however new the registry's version is.</summary>
    [Fact]
    public void DownloadedBricksAreNeitherInstalledNorUpdatable()
    {
        var store = new PackageStore(Path.Combine(root, "store"));
        Directory.CreateDirectory(store.Root);
        Directory.Move(BrickService.New(root, "user.mateo.aside"), Path.Combine(store.Root, "user.mateo.aside@abc"));
        var used = Resolved(BrickService.New(root, "user.mateo.used"));

        var catalog = BrickCatalog.WithRegistries(BrickCatalog.Local([used], null, store, Reserved),
            [Listing("user.mateo.aside", "9.0.0"), Listing("user.mateo.used", "9.0.0")]);

        Assert.Equal(["user.mateo.used"], BrickCatalog.Filter(catalog, BrickFilter.Installed, null).Select(static b => b.Id));
        Assert.Equal(["user.mateo.aside"], BrickCatalog.Filter(catalog, BrickFilter.Available, null).Select(static b => b.Id));
        Assert.Equal(["user.mateo.used"], BrickCatalog.Filter(catalog, BrickFilter.Updates, null).Select(static b => b.Id));
    }

    /// <summary>
    /// A brick that exists only in a registry is listed with the store's name and scopes, so searching its name finds
    /// it, and a brick of the other scope is told apart rather than hidden.
    /// </summary>
    [Fact]
    public void RegistryOnlyBricksAreFoundByTheirPresentation()
    {
        var studioOnly = new PackageManifest
        {
            Name = "user.mateo.inspector",
            Version = SemanticVersion.Parse("1.0.2"),
            DisplayName = "Wire Inspector",
            Description = "Traces events",
            Scopes = [PackageScope.Studio],
        };

        var catalog = BrickCatalog.WithRegistries(BrickCatalog.Local([], null, null, Reserved),
            [Listing("user.mateo.inspector", "1.0.2", "1.0.1") with { ManifestJson = studioOnly.ToJson() }], Reserved);

        var found = Assert.Single(BrickCatalog.Filter(catalog, BrickFilter.All, "wire insp"));
        Assert.Equal("Wire Inspector", found.DisplayName);
        Assert.Equal(BrickState.Available, found.State);
        Assert.False(found.Accepts(PackageScope.Project));
        Assert.True(found.Accepts(PackageScope.Studio));
        Assert.Equal(["1.0.2", "1.0.1"], found.Versions.Select(static v => v.Version));
    }

    /// <summary>A newer registry presentation replaces the name a stored copy carried, so renaming it upstream shows on refresh.</summary>
    [Fact]
    public void RegistryPresentationRenamesStoredBricks()
    {
        var store = new PackageStore(Path.Combine(root, "store"));
        Directory.CreateDirectory(store.Root);
        Directory.Move(BrickService.New(root, "user.mateo.look"), Path.Combine(store.Root, "user.mateo.look@abc"));
        var renamed = new PackageManifest { Name = "user.mateo.look", Version = SemanticVersion.Parse("0.2.0"), DisplayName = "Retro Look" };

        var catalog = BrickCatalog.WithRegistries(BrickCatalog.Local([], null, store, Reserved),
            [Listing("user.mateo.look", "0.2.0") with { ManifestJson = renamed.ToJson() }], Reserved);

        Assert.Equal("Retro Look", Assert.Single(catalog).DisplayName);
    }

    /// <summary>A bake twin published under an id of its own is a form of its source, listed in the source's row.</summary>
    [Fact]
    public void TwinsWithTheirOwnIdJoinTheirSource()
    {
        var store = new PackageStore(Path.Combine(root, "store"));
        Directory.CreateDirectory(store.Root);
        Directory.Move(BrickService.New(root, "user.mateo.save"), Path.Combine(store.Root, "user.mateo.save@a"));
        var twin = BrickService.New(root, "user.mateo.save.bake");
        new PackageManifest
        {
            Name = "user.mateo.save.bake",
            Version = SemanticVersion.Parse("0.1.0"),
            BakeOf = new PackageBakeOf { Brick = "user.mateo.save", Version = SemanticVersion.Parse("0.1.0"), Integrity = "sha256-x" },
        }.Save(twin);
        Directory.Move(twin, Path.Combine(store.Root, "user.mateo.save.bake@b"));

        var brick = Assert.Single(BrickCatalog.Local([], null, store, Reserved));

        Assert.Equal("user.mateo.save", brick.Id);
        Assert.Equal(["bake", "source"], brick.Downloads.Select(static d => d.Variant).Order(StringComparer.Ordinal));
    }

    static RegistryListing Listing(string id, params string[] versions) =>
        new("bricks.example", id, [.. versions.Select(static v => new ListedVersion(v, Yanked: false))]);

    static ResolvedPackage Resolved(string folder)
    {
        var manifest = PackageManifest.Load(folder, ["turian"]);
        return new ResolvedPackage(manifest.Name, manifest, folder, PackageOrigin.Embedded, "embedded", null, null, 1, false);
    }
}
