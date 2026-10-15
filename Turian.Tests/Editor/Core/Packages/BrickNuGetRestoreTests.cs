namespace Turian.Tests;

/// <summary>The merged stub project restoring loaded bricks' NuGet maps into the project's cache.</summary>
public sealed class BrickNuGetRestoreTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-brick-nuget-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);

    /// <summary>Bricks without packages restore nothing and write no stub.</summary>
    [Fact]
    public void BricksWithoutPackagesRestoreNothing()
    {
        var packages = BrickNuGetRestore.EnsureRestored(
            [Brick("a.one")], project, NullLogger.Instance);

        Assert.Empty(packages);
        Assert.False(Directory.Exists(Path.Combine(project, "Bricks", BrickNuGetRestore.RestoreDirectoryName)));
    }

    /// <summary>The stub pins every unified package and enables the lock file.</summary>
    [Fact]
    public void StubPinsUnifiedPackagesWithLockFile()
    {
        var directory = Directory.CreateTempSubdirectory("turian-brick-stub-").FullName;
        try
        {
            BrickNuGetRestore.WriteStub(directory, new Dictionary<string, string> { ["X"] = "1.4.0" });

            var stub = File.ReadAllText(Path.Combine(directory, BrickNuGetRestore.StubProjectFileName));
            Assert.Contains("<PackageReference Include=\"X\" Version=\"1.4.0\" />", stub);
            Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", stub);
            Assert.Contains("<TargetFramework>net10.0</TargetFramework>", stub);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Only restored package folders are returned, by lowercase id and version.</summary>
    [Fact]
    public void PackageDirectoriesListRestoredFolders()
    {
        var packages = Directory.CreateTempSubdirectory("turian-brick-packages-").FullName;
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(packages, "x", "1.4.0"));

            var directories = BrickNuGetRestore.PackageDirectories(packages,
                new Dictionary<string, string> { ["X"] = "1.4.0", ["Missing"] = "2.0.0" });

            Assert.Equal([Path.Combine(packages, "x", "1.4.0")], directories);
        }
        finally
        {
            Directory.Delete(packages, recursive: true);
        }
    }

    /// <summary>Conflicting maps fail the restore with the unification error.</summary>
    [Fact]
    public void ConflictingMapsFailRestore()
    {
        var error = Assert.Throws<PackageException>(() => BrickNuGetRestore.EnsureRestored(
            [Brick("a.one", ("X", "1.4.0")), Brick("a.two", ("X", "2.0.0"))], project, NullLogger.Instance));

        Assert.Contains("a.one", error.Message);
        Assert.Contains("a.two", error.Message);
    }

    /// <summary>A map already committed beside the brick's assembly restores nothing, so a clone loads without a feed.</summary>
    [Fact]
    public void MapsCoveredByCommittedCopiesRestoreNothing()
    {
        var brick = SyncedBrick([("X", "1.4.0")], ("X", "1.4.0", ["X.dll"]));

        var packages = BrickNuGetRestore.EnsureRestored([brick], project, NullLogger.Instance);

        Assert.Empty(packages);
        Assert.False(Directory.Exists(StubDirectory));
    }

    /// <summary>A package whose copy is missing from the committed payload is restored again.</summary>
    [Fact]
    public void MapsWithAMissingCopyRestore()
    {
        var brick = SyncedBrick([("X", "1.4.0")], ("X", "1.4.0", ["X.dll"]));
        File.Delete(Path.Combine(brick.RootPath, "Precast~", "lib", "X.dll"));

        Assert.Throws<PackageException>(() => RestoreFromEmptyFeed(brick));

        Assert.True(Directory.Exists(StubDirectory));
    }

    /// <summary>A committed copy at another version does not cover the map.</summary>
    [Fact]
    public void CopiesAtAnotherVersionDoNotCoverTheMap()
    {
        var brick = SyncedBrick([("X", "1.4.0")], ("X", "1.2.0", ["X.dll"]));

        Assert.Throws<PackageException>(() => RestoreFromEmptyFeed(brick));

        Assert.True(Directory.Exists(StubDirectory));
    }

    /// <summary>A package that carries no managed assembly is covered by an empty file list.</summary>
    [Fact]
    public void PackagesWithoutManagedAssembliesCountAsCovered()
    {
        var brick = SyncedBrick([("X", "1.4.0"), ("X.Native", "1.4.0")],
            ("X", "1.4.0", ["X.dll"]), ("X.Native", "1.4.0", []));

        Assert.Empty(BrickNuGetRestore.EnsureRestored([brick], project, NullLogger.Instance));
    }

    /// <summary>The sync record lists what each mapped package contributed, whether or not it carried an assembly.</summary>
    [Fact]
    public void SyncedPackagesReadEveryMappedPackage()
    {
        var brick = SyncedBrick([("X", "1.4.0"), ("X.Native", "1.4.0")],
            ("X", "1.4.0", ["X.dll"]), ("X.Native", "1.4.0", []));

        var synced = BrickNuGetRestore.SyncedPackages(brick.RootPath);

        Assert.Equal("1.4.0", synced["X"].Version);
        Assert.Equal(["X.dll"], synced["X"].Files);
        Assert.Empty(synced["X.Native"].Files);
    }

    string StubDirectory => Path.Combine(project, "Bricks", BrickNuGetRestore.RestoreDirectoryName);

    /// <summary>A lock file pinning the map is kept, so later restores stay locked against it.</summary>
    [Fact]
    public void AMatchingLockFileSurvives()
    {
        var directory = WriteLock(
            """{ "version": 1, "dependencies": { "net10.0": { "X": { "type": "Direct", "resolved": "1.4.0" } } } }""");

        Assert.False(BrickNuGetRestore.DropStaleLockFile(directory, new Dictionary<string, string> { ["X"] = "1.4.0" }));

        Assert.True(File.Exists(Path.Combine(directory, BrickNuGetRestore.LockFileName)));
    }

    /// <summary>Transitive packages in the lock file are the direct ones' business, so it is kept.</summary>
    [Fact]
    public void TransitiveEntriesDoNotMakeALockFileStale()
    {
        var directory = WriteLock(
            """
            { "version": 1, "dependencies": { "net10.0": {
              "X": { "type": "Direct", "resolved": "1.4.0" },
              "X.Dep": { "type": "Transitive", "resolved": "4.0.0" } } } }
            """);

        Assert.False(BrickNuGetRestore.DropStaleLockFile(directory, new Dictionary<string, string> { ["X"] = "1.4.0" }));
    }

    /// <summary>A lock file left over from another map is deleted, or the bricks could never restore again.</summary>
    [Fact]
    public void AStaleLockFileIsDeleted()
    {
        var directory = WriteLock(
            """{ "version": 1, "dependencies": { "net10.0": { "X": { "type": "Direct", "resolved": "1.2.0" } } } }""");

        Assert.True(BrickNuGetRestore.DropStaleLockFile(directory, new Dictionary<string, string> { ["X"] = "1.4.0" }));

        Assert.False(File.Exists(Path.Combine(directory, BrickNuGetRestore.LockFileName)));
    }

    /// <summary>A lock file naming another package set no longer describes the map.</summary>
    [Fact]
    public void ALockFileWithAnotherPackageSetIsDeleted()
    {
        var directory = WriteLock(
            """{ "version": 1, "dependencies": { "net10.0": { "Y": { "type": "Direct", "resolved": "1.4.0" } } } }""");

        Assert.True(BrickNuGetRestore.DropStaleLockFile(directory,
            new Dictionary<string, string> { ["X"] = "1.4.0", ["Y"] = "1.4.0" }));

        Assert.False(File.Exists(Path.Combine(directory, BrickNuGetRestore.LockFileName)));
    }

    static string WriteLock(string json)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"brick-lock-{Guid.NewGuid():N}"))
            .FullName;
        File.WriteAllText(Path.Combine(directory, BrickNuGetRestore.LockFileName), json);
        return directory;
    }

    // A restore against an empty feed fails fast and offline, so the test proves a restore was attempted.
    IReadOnlyList<string> RestoreFromEmptyFeed(ResolvedPackage brick) => BrickNuGetRestore.EnsureRestored([brick], project,
        NullLogger.Instance, "desktop", [Directory.CreateDirectory(Path.Combine(project, "empty-feed")).FullName]);

    // A brick whose payload holds the copies a sync made, which is what a fresh clone has.
    static ResolvedPackage SyncedBrick((string Package, string Version)[] nuget,
        params (string Package, string Version, string[] Files)[] synced)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"brick-{Guid.NewGuid():N}")).FullName;
        var lib = Directory.CreateDirectory(Path.Combine(folder, "Precast~", "lib")).FullName;
        var packages = new JsonObject();
        foreach (var (package, version, files) in synced)
        {
            foreach (var file in files)
            {
                File.Copy(typeof(BrickNuGet).Assembly.Location, Path.Combine(lib, file));
            }

            packages[package] = new JsonObject
            {
                ["version"] = version,
                ["files"] = new JsonArray([.. files.Select(static file => (JsonNode)file)]),
            };
        }

        File.WriteAllText(Path.Combine(folder, "Precast~", BrickNuGetRestore.SyncRecordFileName),
            new JsonObject { ["packages"] = packages }.ToJsonString());

        var manifest = new PackageManifest { Name = "a.synced", Version = SemanticVersion.Parse("0.1.0") };
        foreach (var (package, version) in nuget)
        {
            manifest.Nuget[package] = version;
        }

        return new ResolvedPackage(manifest.Name, manifest, folder, PackageOrigin.Builtin, "builtin:a.synced", null, null,
            1, false);
    }

    static ResolvedPackage Brick(string id, params (string Package, string Version)[] nuget)
    {
        var manifest = new PackageManifest { Name = id, Version = SemanticVersion.Parse("0.1.0") };
        foreach (var (package, version) in nuget)
        {
            manifest.Nuget[package] = version;
        }

        return new ResolvedPackage(id, manifest, "/", PackageOrigin.Builtin, $"builtin:{id}", null, null, 1, false);
    }
}
