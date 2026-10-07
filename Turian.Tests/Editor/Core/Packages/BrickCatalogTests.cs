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
            File.SetAttributes(file, FileAttributes.Normal);
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

        var catalog = BrickCatalog.Local([used], builtins, store, Reserved).ToDictionary(b => b.Id);

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

        var catalog = BrickCatalog.Local([], null, store, Reserved).ToDictionary(b => b.Id);

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
            [("bricks.example", "user.mateo.old", "0.1.0")]).ToDictionary(b => b.Id);

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

        var catalog = BrickCatalog.WithRegistries(local,
            [("bricks.example", "user.mateo.used", "9.0.0"), ("bricks.example", "user.mateo.remote", "1.2.0")])
            .ToDictionary(b => b.Id);

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

        Assert.Equal(["user.mateo.used"], BrickCatalog.Filter(catalog, BrickFilter.Installed, null).Select(b => b.Id));
        Assert.Equal(["user.mateo.builtin"], BrickCatalog.Filter(catalog, BrickFilter.Available, null).Select(b => b.Id));
        Assert.Equal(["user.mateo.builtin"], BrickCatalog.Filter(catalog, BrickFilter.All, "BUILTIN").Select(b => b.Id));
        Assert.Empty(BrickCatalog.Filter(catalog, BrickFilter.Updates, null));
    }

    static ResolvedPackage Resolved(string folder)
    {
        var manifest = PackageManifest.Load(folder, ["turian"]);
        return new ResolvedPackage(manifest.Name, manifest, folder, PackageOrigin.Embedded, "embedded", null, null, 1, false);
    }
}
