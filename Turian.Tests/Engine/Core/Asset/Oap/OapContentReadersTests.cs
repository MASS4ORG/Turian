namespace Turian.Tests;

/// <summary>Opening Open Asset Packages over engine content sources.</summary>
public class OapContentReadersTests
{
    /// <summary>A reader over a borrowed source leaves it open; an owning reader closes it with its streams.</summary>
    [Fact]
    public void ContentSourceOwnershipFollowsOpenFlag()
    {
        var id = Guid.NewGuid();
        var writer = new OapWriter();
        writer.Add(id, "payload"u8.ToArray(), "a.txt", compression: OapCompressChoice.Fixed(OapCompression.Store));
        var bytes = writer.Serialize();

        var borrowed = new MemoryContentSource(bytes, new ContentIdentity("a", "1"));
        using (var reader = OapReader.Open(borrowed))
        {
            Assert.Equal("payload"u8.ToArray(), reader.ReadAsset(reader.FindById(id)!.Value));
        }

        Assert.False(borrowed.Disposed);

        var path = Path.Combine(Path.GetTempPath(), $"turian-oap-{Guid.NewGuid():N}.oap");
        writer.WriteToFile(path);
        try
        {
            var owned = FileContentSource.Open(path);
            var owner = OapReader.Open(owned, ownsSource: true);
            var stream = owner.OpenAssetStream(owner.FindById(id)!.Value);
            Assert.Equal("payload", new StreamReader(stream, leaveOpen: true).ReadToEnd());
            owner.Dispose();
            stream.Position = 0;
            Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
