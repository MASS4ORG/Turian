namespace Turian.Tests;

/// <summary>The <c>.brick</c> transport file: reproducible packing, extraction and archive sources.</summary>
public sealed class BrickArchiveTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-brick-{Guid.NewGuid():N}");

    /// <summary>Creates the scratch folder.</summary>
    public BrickArchiveTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>Packing the same folder twice yields the same bytes, and writes the hash beside the file.</summary>
    [Fact]
    public void PackingIsReproducible()
    {
        var brick = Brick("rules", "com.acme.rules", "1.0.0");

        var first = BrickArchive.Pack(brick, Path.Combine(root, "out1"));
        Thread.Sleep(20);
        File.SetLastWriteTimeUtc(Path.Combine(brick, "Runtime", "Rule.txt"), DateTime.UtcNow);
        var second = BrickArchive.Pack(brick, Path.Combine(root, "out2"));

        Assert.Equal(first.Integrity, second.Integrity);
        Assert.Equal("com.acme.rules-1.0.0.brick", Path.GetFileName(first.Path));
        Assert.Contains(Path.GetFileName(first.Path), File.ReadAllText($"{first.Path}.sha256"), StringComparison.Ordinal);
    }

    /// <summary>Build output, version control and earlier packs stay out of the file.</summary>
    [Fact]
    public void PackingSkipsBuildOutputAndVersionControl()
    {
        var brick = Brick("rules", "com.acme.rules", "1.0.0");
        File.WriteAllText(Path.Combine(brick, ".git-ignored.txt"), "kept");
        Directory.CreateDirectory(Path.Combine(brick, ".git"));
        File.WriteAllText(Path.Combine(brick, ".git", "HEAD"), "ref");
        Directory.CreateDirectory(Path.Combine(brick, "Source~", "bin"));
        File.WriteAllText(Path.Combine(brick, "Source~", "bin", "x.dll"), "binary");
        File.WriteAllText(Path.Combine(brick, "old-0.9.0.brick"), "zip");

        var packed = BrickArchive.Pack(brick, Path.Combine(root, "out"));

        using var zip = ZipFile.OpenRead(packed.Path);
        Assert.Equal([".git-ignored.txt", "Runtime/Rule.txt", "package.json"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    /// <summary>A brick file is read back as the same manifest and extracts to the same files.</summary>
    [Fact]
    public void ArchivesRoundTrip()
    {
        var packed = BrickArchive.Pack(Brick("rules", "com.acme.rules", "2.1.0"), Path.Combine(root, "out"));

        Assert.Equal("2.1.0", BrickArchive.ReadManifest(packed.Path).Version!.ToString());
        var target = Path.Combine(root, "extracted");
        BrickArchive.Extract(packed.Path, target);
        Assert.Equal("rule", File.ReadAllText(Path.Combine(target, "Runtime", "Rule.txt")));
    }

    /// <summary>A file that is not a zip, or has no manifest, is refused with a package error.</summary>
    [Fact]
    public void InvalidArchivesAreRefused()
    {
        var notZip = Path.Combine(root, "bad.brick");
        File.WriteAllText(notZip, "not a zip");
        Assert.Throws<PackageException>(() => BrickArchive.ReadManifest(notZip));

        var empty = Path.Combine(root, "empty.brick");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");
        Assert.Throws<PackageException>(() => BrickArchive.ReadManifest(empty));
    }

    /// <summary>A project installs a brick from its file: read-only in the store, pinned by hash, and checked when locked.</summary>
    [Fact]
    public async Task ProjectsInstallFromBrickFiles()
    {
        var packed = BrickArchive.Pack(Brick("rules", "com.acme.rules", "1.0.0"), Path.Combine(root, "out"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        new ProjectManifest { Dependencies = { ["com.acme.rules"] = $"file:{packed.Path}" } }.Save(project);
        var resolver = new PackageResolver(new PackageStore(Path.Combine(root, "store")));

        var resolution = await resolver.ResolveAsync(project, TestContext.Current.CancellationToken);
        var package = Assert.Single(resolution.Packages);

        Assert.Equal(PackageOrigin.Archive, package.Origin);
        Assert.True(package.IsReadOnly);
        Assert.Equal(packed.Integrity, package.Integrity);
        Assert.StartsWith(Path.Combine(root, "store"), package.RootPath, StringComparison.Ordinal);
        Assert.Equal("rule", File.ReadAllText(Path.Combine(package.RootPath, "Runtime", "Rule.txt")));

        resolution.Lock.Save(project);
        var locked = new PackageResolver(new PackageStore(Path.Combine(root, "store")), new PackageResolverOptions { Locked = true });
        Assert.Single((await locked.ResolveAsync(project, TestContext.Current.CancellationToken)).Packages);

        File.WriteAllText(Path.Combine(root, "rules", "Runtime", "Rule.txt"), "changed");
        BrickArchive.Pack(Path.Combine(root, "rules"), Path.Combine(root, "out"));
        await Assert.ThrowsAsync<PackageException>(() => locked.ResolveAsync(project, TestContext.Current.CancellationToken));
    }

    /// <summary>Declaring, replacing and removing a brick edits only the project manifest.</summary>
    [Fact]
    public void ManifestEditsAddReplaceAndRemove()
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);

        ProjectBricks.Add(project, "com.acme.rules", "file:../rules");
        ProjectBricks.Add(project, "com.acme.rules", "file:../other");
        Assert.Equal("file:../other", ProjectManifest.Load(project).Manifest.Dependencies["com.acme.rules"]);
        Assert.Throws<PackageException>(() => ProjectBricks.Add(project, "Not An Id", "file:../x"));
        Assert.Throws<PackageException>(() => ProjectBricks.Add(project, "com.acme.rules", "what is this"));

        Assert.True(ProjectBricks.Remove(project, "com.acme.rules"));
        Assert.False(ProjectBricks.Remove(project, "com.acme.rules"));
    }

    /// <summary>Embedding copies the brick writable into the project and records where it came from.</summary>
    [Fact]
    public async Task EmbeddingForksTheBrick()
    {
        var packed = BrickArchive.Pack(Brick("rules", "com.acme.rules", "1.0.0"), Path.Combine(root, "out"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        new ProjectManifest { Dependencies = { ["com.acme.rules"] = $"file:{packed.Path}" } }.Save(project);
        var resolution = await new PackageResolver(new PackageStore(Path.Combine(root, "store")))
            .ResolveAsync(project, TestContext.Current.CancellationToken);

        var fork = ProjectBricks.Embed(resolution.Packages[0], project);

        Assert.Equal(Path.Combine(project, "Packages", "com.acme.rules"), fork);
        Assert.False(new FileInfo(Path.Combine(fork, "Runtime", "Rule.txt")).IsReadOnly);
        Assert.Equal($"com.acme.rules@1.0.0 ({packed.Integrity})", PackageManifest.Load(fork).Upstream);
        Assert.Throws<PackageException>(() => ProjectBricks.Embed(resolution.Packages[0], project));
    }

    string Brick(string folder, string id, string version)
    {
        var path = Path.Combine(root, folder);
        Directory.CreateDirectory(Path.Combine(path, "Runtime"));
        new PackageManifest { Name = id, Version = SemanticVersion.Parse(version) }.Save(path);
        File.WriteAllText(Path.Combine(path, "Runtime", "Rule.txt"), "rule");
        return path;
    }
}
