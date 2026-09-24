namespace Turian.Tests;

/// <summary>Round-trip, random-access, encryption and corruption tests for the OAP reader/writer.</summary>
public class OapWriterReaderTests
{
    /// <summary>Assets round-trip through store, deflate and auto compression.</summary>
    [Fact]
    public void RoundTrip_AllCompressionModes()
    {
        var small = "second"u8.ToArray();
        var repetitive = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("AAAA", 400)));
        var random = RandomNumberGenerator.GetBytes(4096);

        var writer = new OapWriter();
        var idSmall = Guid.NewGuid();
        var idRepetitive = Guid.NewGuid();
        var idRandom = Guid.NewGuid();
        writer.Add(idSmall, small, "b.txt", assetType: 5);
        writer.Add(idRepetitive, repetitive, "a.bin", compression: OapCompressChoice.Fixed(OapCompression.Deflate));
        writer.Add(idRandom, random, "c.bin");

        var reader = OapReader.Open(writer.Serialize());

        Assert.Equal(3, reader.Count);
        Assert.True(reader.IsSortedIndex);

        var entrySmall = reader.FindById(idSmall)!.Value;
        Assert.Equal((byte)5, entrySmall.AssetType);
        Assert.Equal(small, reader.ReadAsset(entrySmall));

        var entryRepetitive = reader.FindById(idRepetitive)!.Value;
        Assert.Equal(OapCompression.Deflate, entryRepetitive.Compression);
        Assert.Equal(repetitive, reader.ReadAsset(entryRepetitive));

        var entryRandom = reader.FindById(idRandom)!.Value;
        Assert.Equal(OapCompression.Store, entryRandom.Compression); // auto keeps random data stored
        Assert.Equal(random, reader.ReadAsset(entryRandom));
    }

    /// <summary>An empty asset round-trips.</summary>
    [Fact]
    public void RoundTrip_EmptyAsset()
    {
        var id = Guid.NewGuid();
        var writer = new OapWriter();
        writer.Add(id, [], "empty");

        var reader = OapReader.Open(writer.Serialize());
        var entry = reader.FindById(id)!.Value;
        Assert.Empty(reader.ReadAsset(entry));
    }

    /// <summary>The index is sorted by id and binary search finds hits and reports misses.</summary>
    [Fact]
    public void RandomAccess_BinarySearchAcrossManyAssets()
    {
        var writer = new OapWriter();
        var ids = new List<Guid>();
        for (var i = 0; i < 200; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            writer.Add(id, Encoding.UTF8.GetBytes($"asset-{i}"), $"assets/{i:D3}.txt");
        }

        var reader = OapReader.Open(writer.Serialize());

        // Entries are stored sorted by the raw big-endian id bytes.
        for (var i = 1; i < reader.Count; i++)
        {
            Assert.True(OapAssetIdComparer.Instance.Compare(reader.EntryAt(i - 1).AssetId, reader.EntryAt(i).AssetId) < 0);
        }

        foreach (var id in ids)
        {
            Assert.NotNull(reader.FindById(id));
        }

        Assert.Null(reader.FindById(Guid.NewGuid()));
    }

    /// <summary>Lookup by virtual path and the manifest are preserved verbatim.</summary>
    [Fact]
    public void FindByPath_And_Manifest()
    {
        var writer = new OapWriter();
        var id = Guid.NewGuid();
        writer.Add(id, "payload"u8, "textures/hero/albedo.amtex");
        writer.SetManifest("{\"name\":\"core\",\"version\":\"1.0.0\"}");

        var reader = OapReader.Open(writer.Serialize());

        var entry = reader.FindByPath("textures/hero/albedo.amtex");
        Assert.NotNull(entry);
        Assert.Equal(id, entry.Value.AssetId);
        Assert.Equal(
            "{\"name\":\"core\",\"version\":\"1.0.0\"}",
            Encoding.UTF8.GetString(reader.Manifest!.Value.Span));
    }

    /// <summary>Dependency ids round-trip in declared order.</summary>
    [Fact]
    public void Dependencies_RoundTrip()
    {
        var root = Guid.NewGuid();
        var depA = Guid.NewGuid();
        var depB = Guid.NewGuid();

        var writer = new OapWriter();
        writer.Add(root, "root"u8, "root.prefab", dependencies: [depA, depB]);
        writer.Add(depA, "a"u8, "a");
        writer.Add(depB, "b"u8, "b");

        var reader = OapReader.Open(writer.Serialize());
        var entry = reader.FindById(root)!.Value;

        Assert.Equal([depA, depB], reader.Dependencies(entry));
    }

    /// <summary>An encrypted asset round-trips; the wrong key and no key both fail cleanly.</summary>
    [Theory]
    [InlineData(OapEncryption.Xor)]
    [InlineData(OapEncryption.ChaCha20)]
    public void Encryption_RoundTripAndKeyHandling(OapEncryption codec)
    {
        var secret = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("top secret level layout ", 8)));
        var key = OapCrypto.DeriveKey("open-sesame");
        var id = Guid.NewGuid();

        var writer = new OapWriter();
        writer.SetKey(key);
        writer.Add(id, secret, "level1.dat", encryption: codec);
        var bytes = writer.Serialize();

        var reader = OapReader.Open(bytes);
        var entry = reader.EntryAt(0);
        Assert.Equal(codec, entry.Encryption);

        // The stored blob must not contain the plaintext.
        var storedBlob = bytes.AsSpan((int)entry.DataOffset, (int)entry.StoredSize);
        Assert.Equal(-1, storedBlob.IndexOf("secret"u8));

        Assert.Throws<OapKeyRequiredException>(() => reader.ReadAsset(entry));

        reader.SetKey(OapCrypto.DeriveKey("wrong"));
        Assert.Throws<OapCorruptDataException>(() => reader.ReadAsset(entry));

        reader.SetKey(key);
        Assert.Equal(secret, reader.ReadAsset(entry));
    }

    /// <summary>Flipping a stored byte is caught by the content CRC.</summary>
    [Fact]
    public void Corruption_IsDetected()
    {
        var id = Guid.NewGuid();
        var writer = new OapWriter();
        writer.Add(id, "important payload"u8,
            "p", compression: OapCompressChoice.Fixed(OapCompression.Store));
        var bytes = writer.Serialize();

        var entry = OapReader.Open(bytes).EntryAt(0);
        bytes[entry.DataOffset] ^= 0xFF;

        var reader = OapReader.Open(bytes);
        Assert.Throws<OapCorruptDataException>(() => reader.ReadAsset(reader.EntryAt(0)));
    }

    /// <summary>An overlay package overrides the base; unaffected assets fall through.</summary>
    [Fact]
    public void Overlay_PatchWinsAndBaseFallsThrough()
    {
        var logo = Guid.NewGuid();
        var music = Guid.NewGuid();
        var level = Guid.NewGuid();

        var baseWriter = new OapWriter();
        baseWriter.Add(logo, "LOGO v1"u8, "logo.txt");
        baseWriter.Add(music, "MUSIC v1"u8, "music.txt");
        baseWriter.Add(level, "LEVEL v1"u8, "level1.txt");

        var patchWriter = new OapWriter();
        patchWriter.Add(level, "LEVEL v2"u8, "level1.txt");
        patchWriter.Add(logo, "LOGO v2"u8, "logo.txt");

        var baseReader = OapReader.Open(baseWriter.Serialize());
        var patchReader = OapReader.Open(patchWriter.Serialize());

        string Resolve(Guid id)
        {
            foreach (var reader in new[] { patchReader, baseReader })
            {
                if (reader.FindById(id) is { } entry)
                {
                    return Encoding.UTF8.GetString(reader.ReadAsset(entry));
                }
            }

            return "MISSING";
        }

        Assert.Equal("LOGO v2", Resolve(logo));
        Assert.Equal("LEVEL v2", Resolve(level));
        Assert.Equal("MUSIC v1", Resolve(music));
    }
}
