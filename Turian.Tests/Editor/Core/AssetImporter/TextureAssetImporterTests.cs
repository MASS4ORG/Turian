namespace Turian.Tests;

/// <summary>
/// Tests for the image importer: the asset type it produces, the color space it seeds from the
/// filename role, and the artifacts it bakes per build target.
/// </summary>
public sealed class TextureAssetImporterTests : IDisposable
{
    readonly TextureAssetImporter importer = new();
    readonly string workingDirectory;

    /// <summary>Creates a throwaway directory for source files and import output.</summary>
    public TextureAssetImporterTests()
    {
        workingDirectory = Path.Combine(Path.GetTempPath(), $"turian-texture-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    /// <summary>The importer claims image extensions and leaves everything else alone.</summary>
    [Theory]
    [InlineData("Antenna_Metal_BaseColor.dds", true)]
    [InlineData("image-png.png", true)]
    [InlineData("image-jpg.jpg", true)]
    [InlineData("BistroExterior.fbx", false)]
    [InlineData("scene-01.prefab", false)]
    [InlineData("image-svg.svg", false)]
    public void IsValid_ClaimsImageFilesOnly(string fileName, bool expected) =>
        Assert.Equal(expected, importer.IsValid(fileName));

    /// <summary>
    /// The meta must deserialize as a <see cref="TextureAsset"/>: an
    /// <c>AssetReference&lt;TextureAsset&gt;</c> cannot resolve a bare <see cref="Asset"/>, and
    /// <c>ConfigureTexture</c> only writes to one.
    /// </summary>
    [Fact]
    public void CreateAsset_ProducesATextureAsset()
    {
        var asset = importer.CreateAsset(Path.Combine(workingDirectory, "Antenna_Metal_BaseColor.dds"));

        Assert.IsType<TextureAsset>(asset);
    }

    /// <summary>
    /// Color space is seeded from the filename role, so a folder scan that reaches a texture
    /// before any material does still tags normal and ORM maps linear.
    /// </summary>
    [Theory]
    [InlineData("Antenna_Metal_BaseColor.dds", true)]
    [InlineData("Paris_LightBulb_Emissive.dds", true)]
    [InlineData("Antenna_Metal_Normal.dds", false)]
    [InlineData("Antenna_Metal_Specular.dds", false)]
    public void CreateAsset_SeedsColorSpaceFromTheFilenameRole(string fileName, bool expectedSrgb)
    {
        var asset = Assert.IsType<TextureAsset>(importer.CreateAsset(Path.Combine(workingDirectory, fileName)));

        Assert.Equal(expectedSrgb, asset.IsSrgb);
    }

    /// <summary>Only normal maps carry the DirectX green-channel convention.</summary>
    [Theory]
    [InlineData("Antenna_Metal_Normal.dds", true)]
    [InlineData("Antenna_Metal_BaseColor.dds", false)]
    public void CreateAsset_FlagsNormalMapsForTheGreenFlip(string fileName, bool expected)
    {
        var asset = Assert.IsType<TextureAsset>(importer.CreateAsset(Path.Combine(workingDirectory, fileName)));

        Assert.Equal(expected, asset.FlipGreenChannel);
    }

    /// <summary>
    /// One artifact is baked per declared build target, named so the variants coexist in one
    /// import directory, and the first names the target the editor itself consumes.
    /// </summary>
    [Fact]
    public void ImportToCache_BakesOneArtifactPerBuildTarget()
    {
        var asset = ImportDds("Antenna_Metal_BaseColor.dds", "DXT1", 16, 16, mipMapCount: 5, blockBytes: 8, out var artifacts);

        Assert.Equal(importer.BuildTargets.Count, artifacts.Count);
        Assert.Equal(TextureAssetImporter.ArtifactFileName(TextureBuildTarget.Pc), artifacts[0]);
        Assert.True(File.Exists(Path.Combine(workingDirectory, artifacts[0])));
        Assert.True(asset.IsSrgb);
    }

    /// <summary>DDS blocks reach the container untouched, format and mip chain intact.</summary>
    [Fact]
    public void ImportToCache_PassesBlockDataThroughUnchanged()
    {
        _ = ImportDds("Antenna_Metal_Normal.dds", "ATI2", 16, 16, mipMapCount: 5, blockBytes: 16, out var artifacts);

        var blob = TextureBlob.Load(Path.Combine(workingDirectory, artifacts[0]));

        Assert.Equal(Format.BC5UnormBlock, blob.Format);
        Assert.Equal(16u, blob.Width);
        Assert.Equal(5, blob.Levels.Count);
        Assert.False(blob.IsSrgb);
    }

    /// <summary>
    /// <c>MaxResolution</c> drops leading mip levels and rewrites the base extent, so the capped
    /// levels never reach the cache — not just never reach VRAM.
    /// </summary>
    [Fact]
    public void ImportToCache_MaxResolution_DropsLeadingMipLevels()
    {
        var asset = Assert.IsType<TextureAsset>(
            importer.CreateAsset(Path.Combine(workingDirectory, "Antenna_Metal_BaseColor.dds")));
        asset.ImportSettings.MaxResolution = 4;

        var artifacts = Import(asset, "Antenna_Metal_BaseColor.dds", DdsFixture.Build("DXT1", 16, 16, 5, 8));
        var blob = TextureBlob.Load(Path.Combine(workingDirectory, artifacts[0]));

        // 16 -> 8 -> 4: two levels skipped, three of the original five remain.
        Assert.Equal(4u, blob.Width);
        Assert.Equal(4u, blob.Height);
        Assert.Equal(3, blob.Levels.Count);
    }

    /// <summary>A per-target override wins over the shared cap for that target.</summary>
    [Fact]
    public void ImportToCache_PerTargetOverride_WinsOverTheSharedCap()
    {
        var asset = Assert.IsType<TextureAsset>(
            importer.CreateAsset(Path.Combine(workingDirectory, "Antenna_Metal_BaseColor.dds")));
        asset.ImportSettings.MaxResolution = 16;
        asset.ImportSettings.PerTarget[TextureBuildTarget.Pc] = new TextureTargetSettings { MaxResolution = 8 };

        var artifacts = Import(asset, "Antenna_Metal_BaseColor.dds", DdsFixture.Build("DXT1", 16, 16, 5, 8));
        var blob = TextureBlob.Load(Path.Combine(workingDirectory, artifacts[0]));

        Assert.Equal(8u, blob.Width);
    }

    /// <summary>A cap at or above the source resolution leaves the chain alone.</summary>
    [Fact]
    public void ImportToCache_MaxResolutionAboveSource_KeepsEveryLevel()
    {
        var asset = Assert.IsType<TextureAsset>(
            importer.CreateAsset(Path.Combine(workingDirectory, "Antenna_Metal_BaseColor.dds")));
        asset.ImportSettings.MaxResolution = 64;

        var artifacts = Import(asset, "Antenna_Metal_BaseColor.dds", DdsFixture.Build("DXT1", 16, 16, 5, 8));
        var blob = TextureBlob.Load(Path.Combine(workingDirectory, artifacts[0]));

        Assert.Equal(16u, blob.Width);
        Assert.Equal(5, blob.Levels.Count);
    }

    /// <summary>A cap smaller than the shortest level still leaves one level to sample.</summary>
    [Fact]
    public void ImportToCache_MaxResolutionBelowTheSmallestLevel_KeepsTheLastLevel()
    {
        var asset = Assert.IsType<TextureAsset>(
            importer.CreateAsset(Path.Combine(workingDirectory, "Antenna_Metal_BaseColor.dds")));
        asset.ImportSettings.MaxResolution = 1;

        var artifacts = Import(asset, "Antenna_Metal_BaseColor.dds", DdsFixture.Build("DXT1", 16, 16, 3, 8));
        var blob = TextureBlob.Load(Path.Combine(workingDirectory, artifacts[0]));

        Assert.Single(blob.Levels);
        Assert.Equal(4u, blob.Width);
    }

    TextureAsset ImportDds(
        string fileName,
        string fourCc,
        uint width,
        uint height,
        uint mipMapCount,
        uint blockBytes,
        out IReadOnlyList<string> artifacts)
    {
        var asset = Assert.IsType<TextureAsset>(importer.CreateAsset(Path.Combine(workingDirectory, fileName)));
        artifacts = Import(asset, fileName, DdsFixture.Build(fourCc, width, height, mipMapCount, blockBytes));
        return asset;
    }

    IReadOnlyList<string> Import(TextureAsset asset, string fileName, byte[] source)
    {
        var sourcePath = Path.Combine(workingDirectory, fileName);
        File.WriteAllBytes(sourcePath, source);
        return importer.ImportToCache(asset, sourcePath, workingDirectory);
    }
}
