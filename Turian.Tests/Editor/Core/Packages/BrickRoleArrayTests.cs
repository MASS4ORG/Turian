namespace Turian.Tests;

/// <summary>Independent payload fragments participate in every consumer and source/twin packing.</summary>
public sealed class BrickRoleArrayTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-role-arrays-{Guid.NewGuid():N}");
    readonly PackageManifest manifest = new()
    {
        Name = "com.acme.fragments",
        UsesId = true,
        Version = SemanticVersion.Parse("1.0.0"),
        Roles = new Dictionary<string, JsonElement>
        {
            [BrickLayout.Precast] = JsonSerializer.SerializeToElement(new[] { "Brick~/First", "Brick~/Second" }),
            [BrickLayout.Source] = JsonSerializer.SerializeToElement(new[] { "Brick~/CodeA", "Brick~/CodeB" }),
            [BrickLayout.Bake] = JsonSerializer.SerializeToElement(new[] { "Brick~/ImportsA", "Brick~/ImportsB" }),
            ["x-extra"] = JsonSerializer.SerializeToElement("Brick~/Extras"),
        },
    };

    /// <summary>Creates two disjoint payloads and their type and NuGet records.</summary>
    public BrickRoleArrayTests()
    {
        Directory.CreateDirectory(root);
        foreach (var part in new[] { "First", "Second" })
        {
            Write($"Brick~/{part}/lib/{part}.dll", "payload");
            Write($"Brick~/{part}/editor/{part}.Editor.dll", "editor payload");
            var types = new UserCodeTypeManifest
            {
                Types = [new UserCodeTypeEntry { FullyQualifiedName = $"Parts.{part}", TypeId = Guid.NewGuid(), Assembly = part }],
            };
            Write($"Brick~/{part}/{BrickAssemblies.TypesFile}", JsonSerializer.Serialize(types));
            Write($"Brick~/{part}/{BrickNuGetRestore.SyncRecordFileName}",
                $$"""{ "packages": { "Dependency.{{part}}": { "version": "1.0.0", "files": ["{{part}}.dll"] } } }""");
            manifest.Nuget[$"Dependency.{part}"] = "1.0.0";
        }

        foreach (var path in new[] { "CodeA/a.cs", "CodeB/b.cs", "ImportsA/cache", "ImportsB/cache", "Extras/data" })
        {
            Write($"Brick~/{path}", "opaque");
        }
        manifest.Save(root);
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Both precast roots contribute runtime/editor references, types, contracts and offline NuGet coverage.</summary>
    [Fact]
    public void ConsumersAggregateEveryDeclaredPayload()
    {
        var brick = Resolved();
        Assert.Equal(["First.dll", "Second.dll"], BrickAssemblies.RuntimeAssemblies(brick).Select(Path.GetFileName));
        Assert.Equal(["First.Editor.dll", "Second.Editor.dll"], BrickAssemblies.EditorAssemblies(brick).Select(Path.GetFileName));
        Assert.Equal(["Parts.First", "Parts.Second"], BrickAssemblies.PrecastTypes(brick).Select(static t => t.FullyQualifiedName));
        Assert.Equal(["Parts.First", "Parts.Second"], BrickContract.Read(root).Types.Select(static t => t.FullName));
        Assert.Equal(["Dependency.First", "Dependency.Second"], BrickNuGetRestore.SyncedPackages(root).Keys);
        Assert.Empty(BrickNuGetRestore.EnsureRestored([brick], Path.Combine(root, "project"), NullLogger.Instance,
            sources: [Path.Combine(root, "nonexistent-feed")]));
        Assert.False(Directory.Exists(Path.Combine(root, "project", "Bricks", "NuGet")));
    }

    /// <summary>Source archives exclude all precast/bake roots; twins exclude every source root and keep opaque data.</summary>
    [Fact]
    public void PairPackingUsesAllRolePaths()
    {
        var (source, twin) = BrickService.PackWithTwin(root, Path.Combine(root, "packed"));
        using var sourceZip = ZipFile.OpenRead(source.Path);
        using var twinZip = ZipFile.OpenRead(twin.Path);
        var sourceNames = sourceZip.Entries.Select(static e => e.FullName).ToList();
        var twinNames = twinZip.Entries.Select(static e => e.FullName).ToList();
        foreach (var path in new[] { "First/lib/First.dll", "Second/lib/Second.dll", "ImportsA/cache", "ImportsB/cache" })
        {
            Assert.DoesNotContain($"Brick~/{path}", sourceNames);
            Assert.Contains($"Brick~/{path}", twinNames);
        }

        foreach (var path in new[] { "CodeA/a.cs", "CodeB/b.cs" })
        {
            Assert.Contains($"Brick~/{path}", sourceNames);
            Assert.DoesNotContain($"Brick~/{path}", twinNames);
        }
        Assert.Contains("Brick~/Extras/data", sourceNames);
        Assert.Contains("Brick~/Extras/data", twinNames);
        Assert.Equal(source.Integrity, twin.Manifest.BakeOf!.Integrity);
    }

    /// <summary>An absent v2 role never causes tools to discover an undeclared default folder.</summary>
    [Fact]
    public void MissingRolesExposeNoPayload()
    {
        manifest.Roles.Remove(BrickLayout.Precast);
        Write("Brick~/Precast/lib/Hidden.dll", "undeclared");
        Assert.Empty(BrickAssemblies.RuntimeAssemblies(Resolved()));
    }

    /// <summary>A corrupt or missing type manifest in one fragment leaves other type manifests available.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("null")]
    public void InvalidTypeFragmentsDoNotHideOtherTypes(string? json)
    {
        var path = Path.Combine(root, "Brick~", "First", BrickAssemblies.TypesFile);
        if (json is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, json);
        }
        Assert.Equal("Parts.Second", Assert.Single(BrickAssemblies.PrecastTypes(Resolved())).FullyQualifiedName);
    }

    /// <summary>A malformed NuGet record does not hide a valid record in another fragment.</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{ \"packages\": { \"Broken\": {} } }")]
    [InlineData("{ \"packages\": { \"NoManagedFiles\": { \"version\": \"1.0.0\" } } }")]
    public void InvalidNuGetFragmentsDoNotHideOtherPackages(string json)
    {
        Write($"Brick~/First/{BrickNuGetRestore.SyncRecordFileName}", json);
        Assert.Contains("Dependency.Second", BrickNuGetRestore.SyncedPackages(root).Keys);
    }

    /// <summary>Refreshing the first generated fragment never deletes assemblies from another fragment.</summary>
    [Fact]
    public void SyncPreservesOtherFragments()
    {
        manifest.Nuget.Clear();
        manifest.Save(root);
        Assert.Empty(BrickService.SyncPrecastDependencies(root, NullLogger.Instance));
        Assert.False(File.Exists(Path.Combine(root, "Brick~", "First", "lib", "First.dll")));
        Assert.True(File.Exists(Path.Combine(root, "Brick~", "Second", "lib", "Second.dll")));
    }

    /// <summary>A generator declares its chosen default folder so readers can discover the generated record.</summary>
    [Fact]
    public void SyncDeclaresAMissingPrecastRole()
    {
        manifest.Nuget.Clear();
        manifest.Roles.Remove(BrickLayout.Precast);
        manifest.Save(root);
        Assert.Empty(BrickService.SyncPrecastDependencies(root, NullLogger.Instance));
        var written = PackageManifest.Load(root);
        Assert.Equal(["Brick~/Precast"], written.RolePaths(BrickLayout.Precast));
        Assert.True(File.Exists(Path.Combine(root, "Brick~", "Precast", BrickNuGetRestore.SyncRecordFileName)));
    }

    /// <summary>A contract exposes no asset id from an invalid meta file.</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{ \"Id\": \"invalid\" }")]
    public void InvalidMetasExposeNoAssetContract(string json)
    {
        Write("Runtime/invalid.txt.meta", json);
        Assert.Empty(BrickContract.Read(root).Assets);
    }

    /// <summary>A twin cannot be packed before its required managed or imported payload exists.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTwinPayloadsAreRejected(bool hasCode)
    {
        manifest.Roles.Remove(BrickLayout.Precast);
        manifest.Roles.Remove(BrickLayout.Bake);
        if (!hasCode)
        {
            manifest.Roles.Remove(BrickLayout.Source);
        }
        manifest.Save(root);
        var error = Assert.Throws<PackageException>(() => BrickService.PackWithTwin(root, Path.Combine(root, "packed")));
        Assert.Contains(hasCode ? "has code but no precast" : "neither code nor baked assets", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A consumable-only brick refuses a separate twin even when compiled payloads are present.</summary>
    [Fact]
    public void ConsumableOnlyBricksRefuseTwins()
    {
        manifest.Bake = PackageManifest.BakeNone;
        manifest.Save(root);
        Assert.Throws<PackageException>(() => BrickService.PackWithTwin(root, Path.Combine(root, "packed")));
    }

    ResolvedPackage Resolved() => new(manifest.Name, manifest, root, PackageOrigin.File, $"file:{root}", null, null, 1, false);

    void Write(string relative, string contents)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
