namespace Turian.Tests;

/// <summary>Copying a brick's assets into the project: new ids, detached by default, optionally remapped.</summary>
public sealed class BrickAssetCopyTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-brick-copy-{Guid.NewGuid():N}");
    readonly string project;
    readonly Guid stats = Guid.NewGuid();
    readonly Guid icon = Guid.NewGuid();

    /// <summary>Creates a brick with a data asset and a texture, and a project that installs it.</summary>
    public BrickAssetCopyTests()
    {
        var brick = Path.Combine(root, "stats");
        Directory.CreateDirectory(Path.Combine(brick, "Runtime"));
        new PackageManifest { Name = "user.mateo.stats", Version = SemanticVersion.Parse("1.0.0") }.Save(brick);
        File.WriteAllText(Path.Combine(brick, "Runtime", "Hero.dataasset"), $$"""{ "__TypeId": "a3000002-0000-4000-8000-000000000001", "Id": "{{stats}}", "Health": 10 }""");
        File.WriteAllText(Path.Combine(brick, "Runtime", "Hero.dataasset.meta"), $$"""{ "__TypeId": "a3000000-0000-4000-8000-000000000006", "RelativePath": "Runtime/Hero.dataasset", "Id": "{{stats}}" }""");
        File.WriteAllBytes(Path.Combine(brick, "Runtime", "Icon.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllText(Path.Combine(brick, "Runtime", "Icon.png.meta"), $$"""{ "__TypeId": "a3000000-0000-4000-8000-00000000000b", "RelativePath": "Runtime/Icon.png", "Id": "{{icon}}" }""");
        Directory.CreateDirectory(Path.Combine(brick, "Samples~"));
        File.WriteAllText(Path.Combine(brick, "Samples~", "Sample.json"), "{}");
        File.WriteAllText(Path.Combine(brick, "Samples~", "Sample.json.meta"), """{ "Id": "00000000-0000-4000-8000-000000000001" }""");

        project = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        File.WriteAllText(Path.Combine(project, "Assets", "Scene.prefab"), $$"""{ "Stats": { "$ref": "{{stats}}" } }""");
        ProjectBricks.Add(project, "user.mateo.stats", "file:../../stats");
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>The brick's assets are the files that ship a meta, leaving out <c>~</c> folders.</summary>
    [Fact]
    public void ListsTheImportableAssets()
    {
        var brick = BrickService.List(project).Single();

        Assert.Equal(["Runtime/Hero.dataasset", "Runtime/Icon.png"], BrickAssetCopy.Assets(brick));
    }

    /// <summary>A copy gets new ids everywhere they appear, a writable file, and leaves the project's references alone.</summary>
    [Fact]
    public void CopiesAreDetached()
    {
        var copies = BrickService.CopyAssets(project, "user.mateo.stats", ["Runtime/Hero.dataasset", "Runtime/Icon.png"], "Stats");

        var hero = copies.Single(c => c.Source == "Runtime/Hero.dataasset");
        Assert.Equal("Assets/Stats/Hero.dataasset", hero.Target);
        Assert.NotEqual(stats, hero.NewId);
        var payload = File.ReadAllText(Path.Combine(project, hero.Target));
        var meta = File.ReadAllText(Path.Combine(project, $"{hero.Target}.meta"));
        Assert.Contains(hero.NewId.ToString(), payload, StringComparison.Ordinal);
        Assert.Contains(hero.NewId.ToString(), meta, StringComparison.Ordinal);
        Assert.DoesNotContain(stats.ToString(), payload + meta, StringComparison.Ordinal);
        Assert.Contains("Assets/Stats/Hero.dataasset", meta, StringComparison.Ordinal);
        Assert.Contains(stats.ToString(), File.ReadAllText(Path.Combine(project, "Assets", "Scene.prefab")), StringComparison.Ordinal);
    }

    /// <summary>With remapping, the project's own files point at the copy instead of the brick's asset.</summary>
    [Fact]
    public void RemappingRepointsProjectFiles()
    {
        var copy = Assert.Single(BrickService.CopyAssets(project, "user.mateo.stats", ["Runtime/Hero.dataasset"], "Stats", remapReferences: true));

        var prefab = File.ReadAllText(Path.Combine(project, "Assets", "Scene.prefab"));
        Assert.Contains(copy.NewId.ToString(), prefab, StringComparison.Ordinal);
        Assert.DoesNotContain(stats.ToString(), prefab, StringComparison.Ordinal);
    }

    /// <summary>Copying twice keeps both copies, and asking for an asset the brick lacks fails.</summary>
    [Fact]
    public void NamesStayUniqueAndMissingAssetsFail()
    {
        _ = BrickService.CopyAssets(project, "user.mateo.stats", ["Runtime/Icon.png"], "Stats");
        var second = Assert.Single(BrickService.CopyAssets(project, "user.mateo.stats", ["Runtime/Icon.png"], "Stats"));

        Assert.Equal("Assets/Stats/Icon 2.png", second.Target);
        Assert.Throws<PackageException>(() => BrickService.CopyAssets(project, "user.mateo.stats", ["Runtime/Nope.png"], "Stats"));
        Assert.Throws<PackageException>(() => BrickService.CopyAssets(project, "user.mateo.other", ["Runtime/Icon.png"], "Stats"));
    }
}
