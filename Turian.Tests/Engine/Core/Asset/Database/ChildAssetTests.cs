namespace Turian.Tests;

/// <summary>
/// Tests for child assets — the materials and textures a model file declares, which have no
/// source file and no <c>.meta</c> of their own and are addressed through their parent.
/// </summary>
public sealed class ChildAssetTests : IDisposable
{
    readonly string projectRoot;
    readonly string assetsRoot;
    readonly AssetDatabase database;

    /// <summary>Creates a throwaway project on disk and a fresh database singleton.</summary>
    public ChildAssetTests()
    {
        TestAssetDatabase.Reset();
        database = new AssetDatabase();

        projectRoot = Path.Combine(Path.GetTempPath(), $"turian-child-assets-{Guid.NewGuid():N}");
        assetsRoot = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assetsRoot);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        TestAssetDatabase.Reset();
        if (Directory.Exists(projectRoot))
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    /// <summary>Writes a source file plus its meta, and registers it as a normal parent asset.</summary>
    Asset CreateParentAsset(string fileName = "model.gltf")
    {
        var sourcePath = Path.Combine(assetsRoot, fileName);
        File.WriteAllText(sourcePath, "{}");

        var asset = new Asset { RelativePath = sourcePath };
        File.WriteAllText($"{sourcePath}.meta", Serializer.Serialize(asset));

        Assert.True(database.RegisterAsset(asset));
        return asset;
    }

    /// <summary>Verifies that a registered child carries a link back to its parent.</summary>
    [Fact]
    public void RegisterChildAsset_LinksChildToParent()
    {
        var parent = CreateParentAsset();
        var material = new MaterialAsset { Id = AssetIdFactory.Derive(parent.Id, "material:0") };

        Assert.True(database.RegisterChildAsset(parent.Id, material));

        Assert.True(database.TryGetAsset(material.Id, out var record));
        Assert.NotNull(record);
        Assert.Equal(parent.Id, record.ParentAssetId);
        Assert.Equal(typeof(MaterialAsset).FullName, record.AssetTypeName);
    }

    /// <summary>Verifies that a child inherits the parent's source path, having none of its own.</summary>
    [Fact]
    public void RegisterChildAsset_InheritsParentSourcePath()
    {
        var parent = CreateParentAsset();
        database.TryGetAsset(parent.Id, out var parentRecord);

        var texture = new TextureAsset { Id = AssetIdFactory.Derive(parent.Id, "image:0") };
        database.RegisterChildAsset(parent.Id, texture);

        Assert.True(database.TryGetAsset(texture.Id, out var childRecord));
        Assert.Equal(parentRecord!.SourceRelativePath, childRecord!.SourceRelativePath);
    }

    /// <summary>Verifies that a child cannot be registered against a parent that is not in the database.</summary>
    [Fact]
    public void RegisterChildAsset_ReturnsFalseWhenParentUnknown()
    {
        var orphan = new MaterialAsset { Id = Guid.NewGuid() };

        Assert.False(database.RegisterChildAsset(Guid.NewGuid(), orphan));
    }

    /// <summary>Verifies that reimporting keeps surviving children and prunes the ones that vanished.</summary>
    [Fact]
    public void RemoveChildAssets_PrunesOnlyChildrenNotInKeepSet()
    {
        var parent = CreateParentAsset();
        var kept = new MaterialAsset { Id = AssetIdFactory.Derive(parent.Id, "material:0") };
        var dropped = new MaterialAsset { Id = AssetIdFactory.Derive(parent.Id, "material:1") };

        database.RegisterChildAsset(parent.Id, kept);
        database.RegisterChildAsset(parent.Id, dropped);

        var pruned = database.RemoveChildAssets(parent.Id, new HashSet<Guid> { kept.Id });

        Assert.Equal(1, pruned);
        Assert.True(database.TryGetAsset(kept.Id, out _));
        Assert.False(database.TryGetAsset(dropped.Id, out _));
        Assert.True(database.TryGetAsset(parent.Id, out _));
    }

    /// <summary>Verifies that passing no keep-set removes every child of the parent.</summary>
    [Fact]
    public void RemoveChildAssets_WithoutKeepSet_RemovesAllChildren()
    {
        var parent = CreateParentAsset();
        database.RegisterChildAsset(parent.Id, new MaterialAsset { Id = AssetIdFactory.Derive(parent.Id, "material:0") });
        database.RegisterChildAsset(parent.Id, new TextureAsset { Id = AssetIdFactory.Derive(parent.Id, "image:0") });

        Assert.Equal(2, database.RemoveChildAssets(parent.Id));
        Assert.Empty(database.GetChildAssets(parent.Id));
    }

    /// <summary>Verifies that one parent's children are not confused with another's.</summary>
    [Fact]
    public void GetChildAssets_ReturnsOnlyThatParentsChildren()
    {
        var first = CreateParentAsset("first.gltf");
        var second = CreateParentAsset("second.gltf");

        database.RegisterChildAsset(first.Id, new MaterialAsset { Id = AssetIdFactory.Derive(first.Id, "material:0") });
        database.RegisterChildAsset(second.Id, new MaterialAsset { Id = AssetIdFactory.Derive(second.Id, "material:0") });
        database.RegisterChildAsset(second.Id, new TextureAsset { Id = AssetIdFactory.Derive(second.Id, "image:0") });

        Assert.Single(database.GetChildAssets(first.Id));
        Assert.Equal(2, database.GetChildAssets(second.Id).Count);
    }

    /// <summary>
    /// Verifies that children survive a database rebuild. They are seeded from the cache catalog
    /// rather than from meta files, so this is the path that keeps them alive between sessions.
    /// </summary>
    [Fact]
    public void BuildDatabase_KeepsChildrenWhenParentStillExists()
    {
        var parent = CreateParentAsset();
        var material = new MaterialAsset { Id = AssetIdFactory.Derive(parent.Id, "material:0") };
        database.RegisterChildAsset(parent.Id, material);
        database.SaveCatalog(projectRoot);

        database.BuildDatabase(assetsRoot);

        Assert.True(database.TryGetAsset(material.Id, out var record));
        Assert.Equal(parent.Id, record!.ParentAssetId);
    }

    /// <summary>
    /// Verifies that deleting the source model takes its materials and textures with it.
    /// Without this the catalog would accumulate children of files that no longer exist.
    /// </summary>
    [Fact]
    public void BuildDatabase_DropsChildrenWhenParentIsDeleted()
    {
        var parent = CreateParentAsset();
        var material = new MaterialAsset { Id = AssetIdFactory.Derive(parent.Id, "material:0") };
        var texture = new TextureAsset { Id = AssetIdFactory.Derive(parent.Id, "image:0") };
        database.RegisterChildAsset(parent.Id, material);
        database.RegisterChildAsset(parent.Id, texture);
        database.SaveCatalog(projectRoot);

        // The user deletes the model: source and meta both go away.
        File.Delete(Path.Combine(assetsRoot, "model.gltf"));
        File.Delete(Path.Combine(assetsRoot, "model.gltf.meta"));

        database.BuildDatabase(assetsRoot);

        Assert.False(database.TryGetAsset(parent.Id, out _));
        Assert.False(database.TryGetAsset(material.Id, out _));
        Assert.False(database.TryGetAsset(texture.Id, out _));
    }

    /// <summary>
    /// Verifies that child ids are stable across reimports, so a scene referencing a material
    /// still resolves after the model is re-exported.
    /// </summary>
    [Fact]
    public void ChildIds_AreStableAcrossReimports()
    {
        var parent = CreateParentAsset();

        var firstImport = AssetIdFactory.Derive(parent.Id, "material:0");
        var secondImport = AssetIdFactory.Derive(parent.Id, "material:0");

        Assert.Equal(firstImport, secondImport);
        Assert.NotEqual(firstImport, AssetIdFactory.Derive(parent.Id, "material:1"));
    }
}
