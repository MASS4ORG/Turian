namespace Turian.Tests;

/// <summary>Checks authoritative primary artifact selection during an editor database rebuild.</summary>
public sealed class AssetManifestPrimaryTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-manifest-primary-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>The manifest selects current content while invalid manifests fall back to available artifacts.</summary>
    [Theory]
    [InlineData(null, "primary.a")]
    [InlineData("null", "primary.a")]
    [InlineData("[]", "primary.a")]
    [InlineData("{}", "primary.a")]
    [InlineData("{\"PrimaryArtifactFileName\":null}", "primary.a")]
    [InlineData("{\"PrimaryArtifactFileName\":\"\"}", "primary.a")]
    [InlineData("{\"PrimaryArtifactFileName\":\"../primary.z\"}", "primary.a")]
    [InlineData("{\"PrimaryArtifactFileName\":\"absent.bin\"}", "primary.a")]
    [InlineData("{", "primary.a")]
    [InlineData("{\"PrimaryArtifactFileName\":\"primary.z\"}", "primary.z")]
    public void DatabaseRebuildUsesManifestPrimary(string? manifest, string expected)
    {
        var (database, asset, cache) = CreateArtifacts();
        if (manifest is not null) File.WriteAllText(Path.Combine(cache, "import.json"), manifest);

        database.BuildDatabase(Path.Combine(root, "Assets"));

        Assert.Equal(expected, Path.GetFileName(database.Assets[asset.Id].ResolveContentPath()));
    }

    /// <summary>A manifest naming another platform's texture cannot override the running platform's variant.</summary>
    [Fact]
    public void DatabaseRebuildRetainsCurrentTextureTargetPreference()
    {
        var (database, asset, cache) = CreateArtifacts();
        var otherTarget = TextureBuildTarget.Current == "windows" ? "linux" : "windows";
        var current = $"primary.{TextureBuildTarget.Current}.texture";
        var other = $"primary.{otherTarget}.texture";
        File.WriteAllText(Path.Combine(cache, current), "current");
        File.WriteAllText(Path.Combine(cache, other), "other");
        File.WriteAllText(Path.Combine(cache, "import.json"),
            JsonSerializer.Serialize(new { PrimaryArtifactFileName = other }));

        database.BuildDatabase(Path.Combine(root, "Assets"));

        Assert.Equal(current, Path.GetFileName(database.Assets[asset.Id].ResolveContentPath()));
    }

    (AssetDatabase Database, Asset Asset, string Cache) CreateArtifacts()
    {
        var assets = Directory.CreateDirectory(Path.Combine(root, "Assets")).FullName;
        var source = Path.Combine(assets, "source.txt");
        File.WriteAllText(source, "source");
        var asset = new Asset { RelativePath = source };
        Serializer.Save(source + ".meta", asset);
        var cache = Directory.CreateDirectory(Path.Combine(root, ".Cache", "Assets", "by-guid", $"{asset.Id:N}"))
            .FullName;
        File.WriteAllText(Path.Combine(cache, "primary.a"), "old");
        File.WriteAllText(Path.Combine(cache, "primary.z"), "current");
        return (new AssetDatabase(), asset, cache);
    }
}
