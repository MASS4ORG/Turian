namespace Gaya.Tests;

/// <summary>Local development links replace registry resolution and retain explicit lock semantics.</summary>
public sealed class ProjectLinkTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-links-{Guid.NewGuid():N}");
    string Project => Path.Combine(root, "project");

    /// <summary>Creates a project and a local brick outside its manifest folder.</summary>
    public ProjectLinkTests()
    {
        Directory.CreateDirectory(Project);
        var brick = Path.Combine(root, "checkout");
        Directory.CreateDirectory(brick);
        new PackageManifest { Name = "com.acme.rules", Version = SemanticVersion.Parse("1.0.0") }.Save(brick);
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A link replaces a registry range and an embedded copy, stays writable and locks only its path.</summary>
    [Fact]
    public async Task SharedLinksReplaceResolutionAndLockThePath()
    {
        var embedded = Path.Combine(Project, "Bricks", "com.acme.rules");
        Directory.CreateDirectory(embedded);
        new PackageManifest { Name = "com.acme.rules", Version = SemanticVersion.Parse("9.0.0") }.Save(embedded);
        new ProjectManifest
        {
            Dependencies = { ["com.acme.rules"] = "^9.0.0" },
            Links = { ["com.acme.rules"] = "../../checkout" },
            Twins = true,
        }.Save(Project);

        var result = await Resolver().ResolveAsync(Project, TestContext.Current.CancellationToken);
        var package = Assert.Single(result.Packages);
        Assert.Equal(Path.Combine(root, "checkout"), package.RootPath);
        Assert.Equal(PackageOrigin.Link, package.Origin);
        Assert.False(package.IsReadOnly);
        Assert.False(result.UsesUserOverride);
        var entry = result.Lock.Dependencies[package.Id];
        Assert.Equal("../../checkout", entry.Link);
        Assert.Null(entry.Version);
        Assert.Null(entry.Integrity);
        Assert.Null(entry.SignedBy);
        result.Lock.Save(Project);
        var locked = await Resolver(locked: true).ResolveAsync(Project, TestContext.Current.CancellationToken);
        Assert.Equal(result.Lock.Serialize(), locked.Lock.Serialize());
    }

    /// <summary>A linked dependency is also used transitively without registry access.</summary>
    [Fact]
    public async Task LinksProvideTransitiveDependencies()
    {
        var shop = Path.Combine(root, "shop");
        Directory.CreateDirectory(shop);
        new PackageManifest
        {
            Name = "com.acme.shop",
            Version = SemanticVersion.Parse("1.0.0"),
            Dependencies = { ["com.acme.rules"] = "^1.0.0" },
        }.Save(shop);
        new ProjectManifest
        {
            Dependencies = { ["com.acme.shop"] = "file:../../shop" },
            Links = { ["com.acme.rules"] = "../../checkout" },
        }.Save(Project);

        var result = await Resolver().ResolveAsync(Project, TestContext.Current.CancellationToken);
        Assert.Equal(["com.acme.rules", "com.acme.shop"], result.Packages.Select(static p => p.Id));
    }

    /// <summary>Removing a shared link preserves the external checkout and removes its project declaration.</summary>
    [Fact]
    public void RemovingALinkPreservesItsExternalFolder()
    {
        new ProjectManifest { Links = { ["com.acme.rules"] = "../../checkout" } }.Save(Project);
        Assert.True(ProjectBricks.Remove(Project, "com.acme.rules"));
        Assert.Empty(ProjectManifest.Load(Project).Manifest.Links);
        Assert.False(ProjectBricks.Remove(Project, "com.acme.rules"));
        Assert.Throws<PackageException>(() => ProjectBricks.Remove(Project, "INVALID"));
        Assert.True(File.Exists(Path.Combine(root, "checkout", PackageManifest.FileName)));
    }

    /// <summary>Unlinking drops the shared and personal links of a brick, keeping the rest of those files and its folder.</summary>
    [Fact]
    public void UnlinkingDropsEveryLinkOfTheBrick()
    {
        new ProjectManifest
        {
            Dependencies = { ["com.acme.rules"] = "^1.0.0" },
            Links = { ["com.acme.rules"] = "../../checkout", ["com.acme.other"] = "../../other" },
        }.Save(Project);
        var user = Path.Combine(Project, "Bricks", ProjectManifest.UserFileName);
        File.WriteAllText(user, """{ "links": { "com.acme.rules": "../../personal" }, "dependencies": { "com.acme.x": "1.0.0" } }""");
        var links = Path.Combine(Project, "Bricks", ProjectManifest.LinksFileName);
        File.WriteAllText(links, """{ "links": { "com.acme.rules": "../../personal" } }""");

        Assert.True(ProjectBricks.Unlink(Project, "com.acme.rules"));

        var (manifest, _) = ProjectManifest.Load(Project);
        Assert.Equal(["com.acme.other"], manifest.Links.Keys);
        Assert.Equal("^1.0.0", manifest.Dependencies["com.acme.rules"]);
        Assert.Contains("com.acme.x", File.ReadAllText(user), StringComparison.Ordinal);
        Assert.DoesNotContain("com.acme.rules", File.ReadAllText(user), StringComparison.Ordinal);
        Assert.DoesNotContain("links", File.ReadAllText(links), StringComparison.Ordinal);
        Assert.False(ProjectBricks.Unlink(Project, "com.acme.rules"));
        Assert.True(File.Exists(Path.Combine(root, "checkout", PackageManifest.FileName)));
    }

    /// <summary>A missing link is reported on ordinary and locked restores rather than using the declared range.</summary>
    [Fact]
    public async Task MissingLinkedFoldersFailLoudly()
    {
        new ProjectManifest
        {
            Dependencies = { ["com.acme.rules"] = "*" },
            Links = { ["com.acme.rules"] = "../../checkout" },
        }.Save(Project);
        (await Resolver().ResolveAsync(Project, TestContext.Current.CancellationToken)).Lock.Save(Project);
        Directory.Move(Path.Combine(root, "checkout"), Path.Combine(root, "moved"));

        foreach (var locked in new[] { false, true })
        {
            var error = await Assert.ThrowsAsync<PackageException>(() => Resolver(locked)
                .ResolveAsync(Project, TestContext.Current.CancellationToken));
            Assert.Contains("linked folder", error.Message, StringComparison.Ordinal);
            Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Personal links override shared paths but are omitted from locked restores and shared saves.</summary>
    [Theory]
    [InlineData(ProjectManifest.LinksFileName)]
    [InlineData(ProjectManifest.UserFileName)]
    public async Task PersonalLinksLeaveTheSharedLockAlone(string fileName)
    {
        new ProjectManifest { Links = { ["com.acme.rules"] = "../../checkout" } }.Save(Project);
        var shared = await Resolver().ResolveAsync(Project, TestContext.Current.CancellationToken);
        shared.Lock.Save(Project);
        var lockBefore = File.ReadAllText(Path.Combine(Project, "Bricks", LockFile.FileName));
        var personal = Path.Combine(root, "personal");
        Directory.CreateDirectory(personal);
        new PackageManifest { Name = "com.acme.rules", Version = SemanticVersion.Parse("1.1.0") }.Save(personal);
        File.WriteAllText(Path.Combine(Project, "Bricks", fileName),
            """{ "links": { "com.acme.rules": "../../personal" } }""");

        var local = await Resolver().ResolveAsync(Project, TestContext.Current.CancellationToken);
        Assert.True(local.UsesUserOverride);
        Assert.Equal(personal, Assert.Single(local.Packages).RootPath);
        var locked = await Resolver(locked: true).ResolveAsync(Project, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(root, "checkout"), Assert.Single(locked.Packages).RootPath);
        Assert.Equal(lockBefore, File.ReadAllText(Path.Combine(Project, "Bricks", LockFile.FileName)));
        var (manifest, _) = ProjectManifest.Load(Project, includeUserOverride: false);
        Assert.Equal("../../checkout", manifest.Links["com.acme.rules"]);
    }

    /// <summary>
    /// Under a personal link, an update of another brick reaches the shared lock, while the linked brick and what only
    /// its committed version needs keep their committed entries.
    /// </summary>
    [Fact]
    public void PersonalLinksKeepTheirEntriesWhileOthersUpdate()
    {
        var committed = new LockFile
        {
            Dependencies =
            {
                ["com.acme.rules"] = Entry("1.0.0", "^1.0.0", "com.acme.helper"),
                ["com.acme.helper"] = Entry("1.0.0", "^1.0.0"),
                ["com.acme.shared"] = Entry("1.0.1", "^1.0.0"),
            },
        };
        var resolved = new LockFile
        {
            Dependencies =
            {
                ["com.acme.rules"] = new LockEntry { Version = SemanticVersion.Parse("1.1.0"), Link = "../../personal" },
                ["com.acme.shared"] = Entry("1.0.2", "^1.0.0"),
            },
        };
        var resolution = new PackageResolution([Package("com.acme.rules", "1.1.0", overridden: true),
            Package("com.acme.shared", "1.0.2", overridden: false)], resolved);

        var shared = resolution.SharedLock(committed);

        Assert.Same(committed.Dependencies["com.acme.rules"], shared.Dependencies["com.acme.rules"]);
        Assert.Same(committed.Dependencies["com.acme.helper"], shared.Dependencies["com.acme.helper"]);
        Assert.Equal(SemanticVersion.Parse("1.0.2"), shared.Dependencies["com.acme.shared"].Version);
        Assert.Same(resolved, (resolution with { Packages = [Package("com.acme.shared", "1.0.2", overridden: false)] })
            .SharedLock(committed));
    }

    static LockEntry Entry(string version, string source, params string[] dependencies)
    {
        var entry = new LockEntry { Version = SemanticVersion.Parse(version), Source = source };
        foreach (var dependency in dependencies)
        {
            entry.Dependencies[dependency] = "^1.0.0";
        }

        return entry;
    }

    static ResolvedPackage Package(string id, string version, bool overridden) =>
        new(id, new PackageManifest { Name = id, Version = SemanticVersion.Parse(version) }, "", PackageOrigin.Registry,
            "^1.0.0", null, null, 1, overridden);

    /// <summary>A failed manifest save restores an embedded brick and leaves the shared declarations intact.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRemovalPreservesTheProject(bool embedded)
    {
        new ProjectManifest { Links = { ["com.acme.rules"] = "../../checkout" } }.Save(Project);
        var folder = Path.Combine(Project, "Bricks", "com.acme.rules");
        if (embedded)
        {
            Directory.CreateDirectory(folder);
            new PackageManifest { Name = "com.acme.rules", Version = SemanticVersion.Parse("1.0.0") }.Save(folder);
        }
        var path = Path.Combine(Project, "Bricks", ProjectManifest.FileName);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => ProjectBricks.Remove(Project, "com.acme.rules"));
            Assert.True(ProjectManifest.Load(Project).Manifest.Links.ContainsKey("com.acme.rules"));
            Assert.Equal(embedded, Directory.Exists(folder));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>Malformed link maps are rejected before resolution can contact a registry.</summary>
    [Theory]
    [InlineData("{ \"links\": null }")]
    [InlineData("{ \"links\": [] }")]
    [InlineData("{ \"links\": {")]
    public void InvalidLinkMapsAreRejected(string json)
    {
        Directory.CreateDirectory(Path.Combine(Project, "Bricks"));
        File.WriteAllText(Path.Combine(Project, "Bricks", ProjectManifest.FileName), json);
        Assert.Throws<PackageException>(() => ProjectManifest.Load(Project));
    }

    /// <summary>Personal registry replacements and additions leave the team's registry declarations intact.</summary>
    [Fact]
    public void PersonalRegistriesLeaveSharedSettingsAlone()
    {
        new ProjectManifest
        {
            ScopedRegistries = [new ScopedRegistry { Name = "team", Url = "https://team.example/v1" }],
        }.Save(Project);
        File.WriteAllText(Path.Combine(Project, "Bricks", ProjectManifest.UserFileName), """
            { "scopedRegistries": [
              { "name": "team", "url": "https://developer.example/v1" },
              { "name": "extra", "url": "https://extra.example/v1" }
            ] }
            """);
        var (personal, _) = ProjectManifest.Load(Project);
        Assert.Equal(["https://developer.example/v1", "https://extra.example/v1"],
            personal.ScopedRegistries.Select(static registry => registry.Url));
        var (shared, _) = ProjectManifest.Load(Project, includeUserOverride: false);
        Assert.Equal("https://team.example/v1", Assert.Single(shared.ScopedRegistries).Url);
    }

    /// <summary>Invalid link IDs, empty paths and folders with another brick's identity are refused.</summary>
    [Theory]
    [InlineData("INVALID", "../../checkout")]
    [InlineData("com.acme.rules", " ")]
    [InlineData("com.acme.other", "../../checkout")]
    public async Task InvalidLinksAreRejected(string id, string path)
    {
        new ProjectManifest { Links = { [id] = path } }.Save(Project);
        await Assert.ThrowsAsync<PackageException>(() => Resolver().ResolveAsync(Project, TestContext.Current.CancellationToken));
    }

    PackageResolver Resolver(bool locked = false) =>
        new(new PackageStore(Path.Combine(root, "store")), new PackageResolverOptions { Locked = locked });
}
