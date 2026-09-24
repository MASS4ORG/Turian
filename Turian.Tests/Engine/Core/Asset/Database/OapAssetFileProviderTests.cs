namespace Turian.Tests;

/// <summary>Tests for <see cref="OapAssetFileProvider"/> resolving assets out of a package.</summary>
public sealed class OapAssetFileProviderTests : IDisposable
{
    readonly string packagePath =
        Path.Combine(Path.GetTempPath(), $"TurianOapProvider_{Guid.NewGuid():N}.oap");

    /// <summary>The provider resolves an asset by the guid embedded in its content key.</summary>
    [Fact]
    public void Provider_ResolvesByContentKeyGuid()
    {
        var id = Guid.NewGuid();
        var payload = "mesh blob bytes"u8.ToArray();

        var writer = new OapWriter();
        writer.Add(id, payload, "meshes/ship.ammesh");
        File.WriteAllBytes(packagePath, writer.Serialize());

        var provider = new OapAssetFileProvider(packagePath, AssetRecord.CreatePrimaryContentKey(id));

        Assert.True(provider.Exists);
        Assert.Equal(payload.Length, provider.Length);
        Assert.Equal(AssetStorageKind.Oap, provider.StorageKind);

        using var stream = provider.GetAssetStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Assert.Equal(payload, buffer.ToArray());
    }

    /// <summary>An unknown asset id reports as absent rather than throwing.</summary>
    [Fact]
    public void Provider_UnknownAsset_DoesNotExist()
    {
        var writer = new OapWriter();
        writer.Add(Guid.NewGuid(), [1, 2, 3], "a");
        File.WriteAllBytes(packagePath, writer.Serialize());

        var provider = new OapAssetFileProvider(
            packagePath,
            AssetRecord.CreatePrimaryContentKey(Guid.NewGuid()));

        Assert.False(provider.Exists);
        Assert.Null(provider.Length);
    }

    /// <summary>Removes the temporary package.</summary>
    public void Dispose()
    {
        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }
    }
}
