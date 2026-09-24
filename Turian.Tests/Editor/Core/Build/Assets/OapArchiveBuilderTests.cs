namespace Turian.Tests;

/// <summary>Tests for <see cref="OapArchiveBuilder"/> packing a project's imported cache.</summary>
public sealed class OapArchiveBuilderTests : IDisposable
{
    readonly string projectRoot =
        Path.Combine(Path.GetTempPath(), $"TurianOapTest_{Guid.NewGuid():N}");

    readonly ILogger logger = Substitute.For<ILogger>();

    /// <summary>Every catalog asset is packed and verifies through the reader.</summary>
    [Fact]
    public void BuildPackage_PacksEveryAsset()
    {
        var texture = AddCachedAsset("Assets/hero.png", "primary.pc.amtex", "Turian.Engine.Core.TextureAsset",
            [1, 2, 3, 4, 5]);
        var mesh = AddCachedAsset("Assets/ship.glb", "primary.ammesh", "Turian.Engine.Core.MeshAsset",
            [.. Enumerable.Repeat((byte)0xAB, 300)]);
        WriteCatalog(texture, mesh);

        var outputPath = Path.Combine(projectRoot, "out.oap");
        var result = new OapArchiveBuilder(logger).BuildPackage(projectRoot, outputPath);

        Assert.Equal(2, result.EntryCount);
        Assert.Equal(string.Empty, result.CatalogFilePath);

        var reader = OapReader.OpenFile(outputPath);
        Assert.Equal(2, reader.Count);

        var textureEntry = reader.FindById(texture.AssetId);
        Assert.NotNull(textureEntry);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, reader.ReadAsset(textureEntry.Value, verify: true));
        Assert.Equal(OapAssetType.Texture, (OapAssetType)textureEntry.Value.AssetType);

        var meshEntry = reader.FindById(mesh.AssetId);
        Assert.NotNull(meshEntry);
        Assert.Equal(OapAssetType.Mesh, (OapAssetType)meshEntry.Value.AssetType);
    }

    /// <summary>Source-code records are never packed.</summary>
    [Fact]
    public void BuildPackage_SkipsSourceCodeRecords()
    {
        var asset = AddCachedAsset("Assets/hero.png", "primary.bin", "Turian.Engine.Core.TextureAsset", [.. "\t\t\t"u8]);
        var script = new AssetRecord
        {
            AssetId = Guid.NewGuid(),
            SourceRelativePath = "Assets/Player.cs",
            ImportedRelativePath = "Assets/Player.cs",
            AssetTypeName = "UserScript"
        };
        WriteCatalog(asset, script);

        var outputPath = Path.Combine(projectRoot, "out.oap");
        var result = new OapArchiveBuilder(logger).BuildPackage(projectRoot, outputPath);

        Assert.Equal(1, result.EntryCount);
        Assert.Null(OapReader.OpenFile(outputPath).FindById(script.AssetId));
    }

    /// <summary>The <c>Build</c> form writes a runtime catalog whose records target the package.</summary>
    [Fact]
    public void Build_WritesRuntimeCatalogWithOapStorageKind()
    {
        var asset = AddCachedAsset("Assets/hero.png", "primary.bin", "Turian.Engine.Core.TextureAsset", [1, 1, 1]);
        WriteCatalog(asset);

        var outputRoot = Path.Combine(projectRoot, "Export");
        var result = new OapArchiveBuilder(logger).Build(projectRoot, outputRoot, new OapPackOptions { Name = "game" });

        Assert.True(File.Exists(result.OapFilePath));
        Assert.EndsWith(Path.Combine("Content", "game.oap"), result.OapFilePath, StringComparison.Ordinal);

        var runtimeCatalog = Serializer.Load<AssetCatalog>(result.CatalogFilePath)!;
        var record = Assert.Single(runtimeCatalog.Records);
        Assert.Equal(AssetStorageKind.Oap, record.StorageKind);
        Assert.Equal(Path.Combine("Content", "game.oap"), record.ImportedRelativePath);
    }

    /// <summary>An encrypted package cannot be read without the passphrase, and can with it.</summary>
    [Fact]
    public void BuildPackage_WithPassphrase_EncryptsEveryAsset()
    {
        var asset = AddCachedAsset("Assets/level.dat", "primary.bin", "LevelData",
            [.. Enumerable.Repeat((byte)0x7F, 128)]);
        WriteCatalog(asset);

        var outputPath = Path.Combine(projectRoot, "secret.oap");
        new OapArchiveBuilder(logger).BuildPackage(
            projectRoot, outputPath, new OapPackOptions { Passphrase = "hunter2" });

        var reader = OapReader.OpenFile(outputPath);
        var entry = reader.EntryAt(0);
        Assert.Equal(OapEncryption.ChaCha20, entry.Encryption);
        Assert.Throws<OapKeyRequiredException>(() => reader.ReadAsset(entry));

        reader.SetKey(OapCrypto.DeriveKey("hunter2"));
        Assert.Equal(Enumerable.Repeat((byte)0x7F, 128).ToArray(), reader.ReadAsset(entry));
    }

    AssetRecord AddCachedAsset(string sourceRelative, string artifactName, string typeName, byte[] payload)
    {
        var id = Guid.NewGuid();
        var importDir = Path.Combine(projectRoot, ".Cache", "Assets", "by-guid", id.ToString("N"));
        Directory.CreateDirectory(importDir);
        var artifactPath = Path.Combine(importDir, artifactName);
        File.WriteAllBytes(artifactPath, payload);

        return new AssetRecord
        {
            AssetId = id,
            ProjectRootPath = projectRoot,
            AssetTypeName = typeName,
            SourceRelativePath = sourceRelative,
            MetaRelativePath = sourceRelative + ".meta",
            PrimaryContentKey = AssetRecord.CreatePrimaryContentKey(id),
            ImportedRelativePath = Path.GetRelativePath(projectRoot, artifactPath),
            StorageKind = AssetStorageKind.LooseFile
        };
    }

    void WriteCatalog(params AssetRecord[] records)
    {
        var catalog = new AssetCatalog { Records = [.. records] };
        var path = Path.Combine(projectRoot, ".Cache", "assetCatalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Serializer.Save(path, catalog);
    }

    /// <summary>Removes the temporary project tree.</summary>
    public void Dispose()
    {
        if (Directory.Exists(projectRoot))
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }
}
