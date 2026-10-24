namespace Gaya.Tests;

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
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

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
        Assert.Equal("com.acme.rules-1.0.0--source.brick", Path.GetFileName(first.Path));
        Assert.Contains(Path.GetFileName(first.Path), File.ReadAllText($"{first.Path}.sha256"), StringComparison.Ordinal);
    }

    /// <summary>Published archives cannot refer to a producer's local filesystem.</summary>
    [Theory]
    [InlineData("file:../rules")]
    [InlineData("file:/tmp/rules.brick")]
    [InlineData("link:../rules")]
    [InlineData("git+../rules#main")]
    [InlineData("git+file:///tmp/rules")]
    [InlineData("C:/work/rules")]
    public void PackingRejectsPathDependencies(string spec)
    {
        var brick = Brick("rules", "com.acme.rules", "1.0.0");
        var manifest = PackageManifest.Load(brick);
        manifest.Dependencies["com.acme.local"] = spec;
        manifest.Save(brick);
        var error = Assert.Throws<PackageException>(() => BrickArchive.Pack(brick, Path.Combine(root, "packed")));
        Assert.Contains("local path", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A project-only links map is rejected even though it is not a brick manifest field.</summary>
    [Fact]
    public void PackingRejectsProjectLinks()
    {
        var brick = Brick("rules", "com.acme.rules", "1.0.0");
        var path = Path.Combine(brick, PackageManifest.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["links"] = new JsonObject { ["com.acme.local"] = "../local" };
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<PackageException>(() => BrickArchive.Pack(brick, Path.Combine(root, "packed")));
    }

    /// <summary>Development-only dependencies must also be portable in a distributed archive.</summary>
    [Fact]
    public void PackingRejectsLocalDevelopmentDependencies()
    {
        var brick = Brick("rules", "com.acme.rules", "1.0.0");
        var manifest = PackageManifest.Load(brick);
        manifest.DevDependencies["com.acme.local"] = "file:../tests";
        manifest.Save(brick);
        Assert.Throws<PackageException>(() => BrickArchive.Pack(brick, Path.Combine(root, "packed")));
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
        Assert.Equal([".git-ignored.txt", "Runtime/Rule.txt", "package.json"], zip.Entries.Select(static e => e.FullName).Order(StringComparer.Ordinal));
    }

    /// <summary>The manifest's exclude globs, and the caller's, leave files and whole folders out of the file.</summary>
    [Fact]
    public void PackingLeavesOutExcludedGlobs()
    {
        var brick = Brick("rules", "com.acme.rules", "1.0.0");
        var manifest = PackageManifest.Load(brick);
        manifest.Exclude = ["Tests~", "*.pdb", "Docs/**/draft-*"];
        manifest.Save(brick);
        foreach (var file in new[] { "Tests~/Rules.Tests/RuleTests.cs", "Runtime/Rule.pdb", "Docs/guide.md", "Docs/a/b/draft-1.md", "Extra/notes.txt" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(brick, file))!);
            File.WriteAllText(Path.Combine(brick, file), "x");
        }

        var packed = BrickArchive.Pack(brick, Path.Combine(root, "out"), exclude: ["Extra"]);

        using var zip = ZipFile.OpenRead(packed.Path);
        Assert.Equal(["Docs/guide.md", "Runtime/Rule.txt", "package.json"], zip.Entries.Select(static e => e.FullName).Order(StringComparer.Ordinal));
    }

    /// <summary>An exclude glob that escapes the folder or would drop the manifest is refused.</summary>
    [Theory]
    [InlineData("../secret")]
    [InlineData("Runtime\\\\Rule.txt")]
    [InlineData("package.json")]
    [InlineData("*.json")]
    public void InvalidExcludeGlobsAreRefused(string glob)
    {
        var brick = Brick("rules", "com.acme.rules", "1.0.0");
        File.WriteAllText(Path.Combine(brick, PackageManifest.FileName),
            $$"""{ "name": "com.acme.rules", "version": "1.0.0", "exclude": [{{System.Text.Json.JsonSerializer.Serialize(glob)}}] }""");

        Assert.Throws<PackageException>(() => PackageManifest.Load(brick));
    }

    /// <summary>Glob syntax: one name per star, any depth for a double star, and a bare name anywhere.</summary>
    [Theory]
    [InlineData("Tests~", "Tests~/a/b.cs", true)]
    [InlineData("Tests~", "Runtime/Tests~/b.cs", true)]
    [InlineData("/Tests~", "Runtime/Tests~/b.cs", false)]
    [InlineData("Runtime/*.cs", "Runtime/a.cs", true)]
    [InlineData("Runtime/*.cs", "Runtime/x/a.cs", false)]
    [InlineData("Runtime/**/*.cs", "Runtime/a.cs", true)]
    [InlineData("Runtime/**/*.cs", "Runtime/x/y/a.cs", true)]
    [InlineData("?.txt", "a.txt", true)]
    [InlineData("?.txt", "ab.txt", false)]
    [InlineData("a.txt", "a_txt", false)]
    public void GlobsMatchLikeGitIgnore(string glob, string path, bool expected) =>
        Assert.Equal(expected, BrickGlob.Matches(glob, path));

    /// <summary>
    /// A twin keeps its source's id and version as a variant, is named after it, and records the source file it was
    /// baked from; the variant is what classifies it, never the file name.
    /// </summary>
    [Fact]
    public void TwinsAreVariantsOfTheirSource()
    {
        var brick = Brick("rules", "com.acme.rules", "1.2.0");
        var source = BrickArchive.Pack(brick, Path.Combine(root, "out"));

        var twin = BrickArchive.Pack(brick, Path.Combine(root, "out"),
            bakeOf: new PackageBakeOf { Brick = "com.acme.rules", Version = source.Manifest.Version, Integrity = source.Integrity },
            variant: "turian3.3");

        var manifest = BrickArchive.ReadManifest(twin.Path);
        Assert.Equal("com.acme.rules-1.2.0--turian3.3.brick", Path.GetFileName(twin.Path));
        Assert.Equal(("com.acme.rules", "turian3.3"), (manifest.Name, manifest.Variant));
        Assert.Equal(source.Integrity, manifest.BakeOf!.Integrity);
        Assert.Equal("1.2.0", manifest.BakeOf.Version!.ToString());
        Assert.Equal("turian3.3", PackageVariant.Of(manifest, twin.Integrity));
        Assert.Equal(PackageVariant.Source, PackageVariant.Of(source.Manifest, source.Integrity));

        File.Move(twin.Path, Path.Combine(root, "out", "renamed.brick"));
        Assert.Equal("turian3.3", BrickArchive.ReadManifest(Path.Combine(root, "out", "renamed.brick")).Variant);
    }

    /// <summary>A bake needs a variant other than source, and a variant cannot change its source's id or version.</summary>
    [Fact]
    public void BakesWithoutAMatchingVariantAreRefused()
    {
        var brick = Brick("rules", "com.acme.rules", "1.2.0");
        var source = BrickArchive.Pack(brick, Path.Combine(root, "out"));
        var bakeOf = new PackageBakeOf { Brick = "com.acme.rules", Version = source.Manifest.Version, Integrity = source.Integrity };

        Assert.Throws<PackageException>(() => BrickArchive.Pack(brick, Path.Combine(root, "a"), bakeOf: bakeOf));
        Assert.Throws<PackageException>(() => BrickArchive.Pack(brick, Path.Combine(root, "b"), bakeOf: bakeOf, variant: PackageVariant.Source));
        Assert.Throws<PackageException>(() => BrickArchive.Pack(brick, Path.Combine(root, "c"), variant: "legacy-x",
            bakeOf: bakeOf));
        var other = new PackageBakeOf { Brick = "com.acme.other", Version = source.Manifest.Version, Integrity = source.Integrity };
        Assert.Contains("keep its source's id", Assert.Throws<PackageException>(() =>
            BrickArchive.Pack(brick, Path.Combine(root, "d"), bakeOf: other, variant: "turian3.3")).Message, StringComparison.Ordinal);
    }

    /// <summary>Manifest variant rules: source never declares bakeOf, every bake does, and keys stay portable.</summary>
    [Theory]
    [InlineData("""{ "id": "com.acme.rules", "version": "1.0.0", "variant": "source" }""", true)]
    [InlineData("""{ "id": "com.acme.rules", "version": "1.0.0", "variant": "turian3.3" }""", false)]
    [InlineData("""{ "id": "com.acme.rules", "version": "1.0.0", "variant": "source", "bakeOf": { "brick": "com.acme.rules", "version": "1.0.0", "integrity": "sha256-x" } }""", false)]
    [InlineData("""{ "id": "com.acme.rules", "version": "1.0.0", "variant": "bad key", "bakeOf": { "brick": "com.acme.rules", "version": "1.0.0", "integrity": "sha256-x" } }""", false)]
    [InlineData("""{ "id": "com.acme.rules", "version": "1.0.0", "variant": "turian3.3", "bakeOf": { "brick": "com.acme.rules", "version": "1.0.1", "integrity": "sha256-x" } }""", false)]
    [InlineData("""{ "id": "com.acme.rules", "version": "1.0.0", "variant": "turian3.3", "bakeOf": { "brick": "com.acme.rules", "version": "1.0.0", "integrity": "sha256-x" } }""", true)]
    [InlineData("""{ "id": "com.acme.rules", "version": "1.0.0", "bakeOf": { "brick": "com.acme.rules", "version": "1.0.0", "integrity": "sha256-x" } }""", false)]
    public void VariantsFollowTheStoreRules(string json, bool valid)
    {
        var parse = () => PackageManifest.Parse(json, "package.json");
        if (valid)
        {
            _ = parse();
        }
        else
        {
            Assert.Throws<PackageException>(parse);
        }
    }

    /// <summary>A bake from before variants gets the store's hash-derived legacy key.</summary>
    [Fact]
    public void LegacyBakesGetTheStoresKey()
    {
        var integrity = $"sha256-{Convert.ToBase64String(Enumerable.Repeat((byte)0xfb, 32).ToArray())}";
        var legacy = PackageManifest.Parse(
            $$"""{ "id": "com.acme.rules.bake", "version": "1.0.0", "bakeOf": { "brick": "com.acme.rules", "version": "1.0.0", "integrity": "{{integrity}}" } }""",
            "package.json");

        // 0xfb bytes encode as "+/v7" in base64: unpadded base64url "-_v7", ending "-_s" for the last two bytes.
        Assert.Equal("legacy-" + string.Concat(Enumerable.Repeat("-_v7", 10)) + "-_s", PackageVariant.Of(legacy, integrity));
        Assert.Equal("engine3.4", PackageVariant.ForHost("engine", SemanticVersion.Parse("3.4.1")));
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
        {
            zip.CreateEntry("readme.txt");
        }

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

        Assert.Equal(Path.Combine(project, "Bricks", "com.acme.rules"), fork);
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
