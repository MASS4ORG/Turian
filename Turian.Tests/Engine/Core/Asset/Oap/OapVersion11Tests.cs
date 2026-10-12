namespace Turian.Tests;

/// <summary>OAP 1.1: chunked blobs, positional ciphers, tombstones, alignment, blob order and package hashes.</summary>
public class OapVersion11Tests
{
    const int BlockSize = 4096;

    /// <summary>Compressible text with one incompressible block, so some blocks are stored raw.</summary>
    static byte[] MixedData(int length)
    {
        var text = string.Concat(Enumerable.Repeat("chunked blocks decode alone. ", length / 29 + 1));
        var data = Encoding.UTF8.GetBytes(text)[..length];
        new Random(11).NextBytes(data.AsSpan(BlockSize, BlockSize));
        return data;
    }

    /// <summary>Chunked blobs round-trip with either codec, and a random block is stored raw.</summary>
    [Theory]
    [InlineData(OapCompression.Deflate)]
    [InlineData(OapCompression.Zstd)]
    public void Chunked_RoundTripsAndStoresIncompressibleBlocksRaw(OapCompression codec)
    {
        var data = MixedData(BlockSize * 3 + 123);
        var id = Guid.NewGuid();
        var writer = new OapWriter();
        writer.Add(id, data, "audio/track.ogg", compression: OapCompressChoice.Chunked(codec, BlockSize));

        var bytes = writer.Serialize();
        var reader = OapReader.Open(bytes);
        var entry = reader.FindById(id)!.Value;

        Assert.True(entry.IsChunked);
        Assert.Equal(codec, entry.Compression);
        Assert.Equal((byte)(0x80 | (byte)codec), bytes[(int)reader.Header.IndexOffset + 44]);
        Assert.Equal(data, reader.ReadAsset(entry));

        var table = OapBlockTable.Parse(bytes.AsSpan((int)entry.DataOffset, (int)entry.StoredSize),
            entry.UncompressedSize);
        Assert.Equal(4, table.BlockCount);
        Assert.True(table.IsRaw(1));
        Assert.False(table.IsRaw(0));
        Assert.Equal(123, table.PlainLength(3));
    }

    /// <summary>Range reads and streams over chunked, encrypted assets return exactly the plaintext slices.</summary>
    [Theory]
    [InlineData(OapEncryption.None)]
    [InlineData(OapEncryption.Xor)]
    [InlineData(OapEncryption.ChaCha20)]
    public void Chunked_RangeReadsAcrossBlocks(OapEncryption encryption)
    {
        var data = MixedData(BlockSize * 3 + 123);
        var id = Guid.NewGuid();
        var key = OapCrypto.DeriveKey("range");
        var writer = new OapWriter();
        writer.SetKey(key);
        writer.Add(id, data, compression: OapCompressChoice.Chunked(OapCompression.Zstd, BlockSize),
            encryption: encryption);
        var reader = OapReader.Open(writer.Serialize());
        reader.SetKey(key);
        var entry = reader.FindById(id)!.Value;

        foreach (var (offset, length) in new[] { (0, 10), (BlockSize - 5, 10), (BlockSize * 2 + 7, BlockSize + 50) })
        {
            var buffer = new byte[length];
            Assert.Equal(length, reader.ReadAssetRange(entry, offset, buffer));
            Assert.Equal(data.AsSpan(offset, length).ToArray(), buffer);
        }

        var tail = new byte[500];
        Assert.Equal(123, reader.ReadAssetRange(entry, BlockSize * 3, tail));
        Assert.Equal(0, reader.ReadAssetRange(entry, data.Length, tail));

        using var stream = reader.OpenAssetStream(entry);
        stream.Seek(-200, SeekOrigin.End);
        var end = new byte[200];
        stream.ReadExactly(end);
        Assert.Equal(data[^200..], end);
        stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(data, copy.ToArray());
    }

    /// <summary>A damaged block fails its own CRC on a partial read, without decoding the rest.</summary>
    [Fact]
    public void Chunked_DamagedBlockFailsItsCrc()
    {
        var data = MixedData(BlockSize * 3);
        var writer = new OapWriter();
        writer.Add(Guid.NewGuid(), data, compression: OapCompressChoice.Chunked(OapCompression.Deflate, BlockSize));
        var bytes = writer.Serialize();
        var entry = OapReader.Open(bytes).EntryAt(0);
        var table = OapBlockTable.Parse(bytes.AsSpan((int)entry.DataOffset, (int)entry.StoredSize),
            entry.UncompressedSize);
        bytes[(int)entry.DataOffset + (int)table.StoredOffset(1) + 10] ^= 0xFF;

        var reader = OapReader.Open(bytes);
        Assert.Equal(10, reader.ReadAssetRange(reader.EntryAt(0), 0, new byte[10]));
        Assert.Throws<OapCorruptDataException>(() => reader.ReadAssetRange(reader.EntryAt(0), BlockSize, new byte[10]));
    }

    /// <summary>Malformed chunk prefixes and invalid encode arguments are rejected.</summary>
    [Fact]
    public void Chunked_InvalidLayoutsAreRejected()
    {
        var prefix = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 3000);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 1);
        Assert.Throws<OapCorruptDataException>(() => OapBlockTable.ReadPrefix(prefix, 100));
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 2);
        Assert.Throws<OapCorruptDataException>(() => OapBlockTable.ReadPrefix(prefix, 100));
        Assert.Throws<OapTruncatedException>(() => OapBlockTable.ReadPrefix(prefix.AsSpan(0, 4), 100));

        Assert.Throws<ArgumentException>(() => OapChunks.Encode([], OapCompression.Zstd, BlockSize));
        Assert.Throws<ArgumentException>(() => OapChunks.Encode("x"u8, OapCompression.Store, BlockSize));
        Assert.Throws<ArgumentOutOfRangeException>(() => OapChunks.Encode("x"u8, OapCompression.Zstd, 5000));

        var blob = OapChunks.Encode(MixedData(BlockSize * 2), OapCompression.Deflate, BlockSize);
        Assert.Throws<OapCorruptDataException>(
            () => OapBlockTable.Parse(blob.AsSpan(0, blob.Length - 1), BlockSize * 2));
    }

    /// <summary>A chunked entry without a codec (0x80) is reported as corrupt rather than returned.</summary>
    [Fact]
    public void Chunked_StoreIsInvalid()
    {
        var writer = new OapWriter();
        writer.Add(Guid.NewGuid(), "plain"u8, compression: OapCompressChoice.Fixed(OapCompression.Store));
        var bytes = writer.Serialize();
        var reader = OapReader.Open(bytes);
        var at = (int)reader.Header.IndexOffset;
        bytes[at + 44] = 0x80;

        var tampered = OapReader.Open(bytes);
        Assert.Throws<OapCorruptDataException>(() => tampered.ReadAsset(tampered.EntryAt(0)));
    }

    /// <summary>Both ciphers decrypt any range on its own, matching a whole-blob pass.</summary>
    [Theory]
    [InlineData(OapEncryption.Xor)]
    [InlineData(OapEncryption.ChaCha20)]
    public void Encryption_IsPositional(OapEncryption codec)
    {
        var key = OapCrypto.DeriveKey("positional");
        var id = new byte[16];
        var whole = RandomNumberGenerator.GetBytes(1000);
        var parts = whole.ToArray();
        OapCrypto.Apply(codec, key, id, 42, whole);
        foreach (var (start, end) in new[] { (0, 63), (63, 64), (64, 200), (200, 1000) })
            OapCrypto.Apply(codec, key, id, 42, parts.AsSpan(start, end - start), start);

        Assert.Equal(whole, parts);
    }

    /// <summary>Stored, encrypted assets stream from the package and decrypt by position.</summary>
    [Fact]
    public void StoredEncrypted_StreamsInRanges()
    {
        var data = RandomNumberGenerator.GetBytes(10_000);
        var key = OapCrypto.DeriveKey("stream");
        var writer = new OapWriter();
        writer.SetKey(key);
        writer.Add(Guid.NewGuid(), data, compression: OapCompressChoice.Fixed(OapCompression.Store),
            encryption: OapEncryption.ChaCha20);
        var reader = OapReader.Open(writer.Serialize());

        Assert.Throws<OapKeyRequiredException>(() => reader.OpenAssetStream(reader.EntryAt(0)));
        reader.SetKey(key);
        using var stream = reader.OpenAssetStream(reader.EntryAt(0));
        stream.Position = 9_000;
        var tail = new byte[1_000];
        stream.ReadExactly(tail);
        Assert.Equal(data[9_000..], tail);
    }

    /// <summary>A whole-blob compressed asset still serves ranges by decoding it once.</summary>
    [Fact]
    public void WholeBlob_RangeReadDecodesFirst()
    {
        var data = MixedData(BlockSize * 2);
        var writer = new OapWriter();
        writer.Add(Guid.NewGuid(), data, compression: OapCompressChoice.Fixed(OapCompression.Zstd));
        var reader = OapReader.Open(writer.Serialize());

        var buffer = new byte[100];
        Assert.Equal(100, reader.ReadAssetRange(reader.EntryAt(0), 50, buffer));
        Assert.Equal(data[50..150], buffer);
        Assert.Equal(0, reader.ReadAssetRange(reader.EntryAt(0), data.Length, buffer));
    }

    /// <summary>Tombstones carry no data, set the header flag and read as "not found".</summary>
    [Fact]
    public void Tombstone_IsFlaggedAndUnreadable()
    {
        var removed = Guid.NewGuid();
        var writer = new OapWriter();
        writer.Add(Guid.NewGuid(), "kept"u8);
        writer.AddTombstone(removed, "old/asset.txt");
        var reader = OapReader.Open(writer.Serialize());

        Assert.True((reader.Header.Flags & OapFlags.HasTombstones) != 0);
        var entry = reader.FindByPath("old/asset.txt")!.Value;
        Assert.True(entry.IsTombstone);
        Assert.Equal(removed, entry.AssetId);
        Assert.Equal(0ul, entry.DataOffset + entry.StoredSize + entry.UncompressedSize + entry.ContentCrc32);
        Assert.Throws<OapNotFoundException>(() => reader.ReadAsset(entry));
        Assert.Throws<OapNotFoundException>(() => reader.OpenAssetStream(entry));
        Assert.Throws<OapNotFoundException>(() => reader.ReadAssetRange(entry, 0, new byte[1]));
    }

    /// <summary>Large blobs start on 4 KiB boundaries and small ones on 16 bytes, behind zero padding.</summary>
    [Fact]
    public void Alignment_PadsBlobsWithZeros()
    {
        var large = RandomNumberGenerator.GetBytes(70_000);
        var small = RandomNumberGenerator.GetBytes(100);
        var writer = new OapWriter { Alignment = OapAlignment.Default };
        var smallId = Guid.NewGuid();
        var largeId = Guid.NewGuid();
        writer.Add(smallId, small, compression: OapCompressChoice.Fixed(OapCompression.Store));
        writer.Add(largeId, large, compression: OapCompressChoice.Fixed(OapCompression.Store));
        writer.Add(Guid.NewGuid(), RandomNumberGenerator.GetBytes(3),
            compression: OapCompressChoice.Fixed(OapCompression.Store));

        var bytes = writer.Serialize();
        var reader = OapReader.Open(bytes);
        var largeEntry = reader.FindById(largeId)!.Value;
        Assert.Equal(0ul, largeEntry.DataOffset % 4096);
        Assert.All(reader.Entries, entry => Assert.Equal(0ul, entry.DataOffset % 16));
        Assert.All(bytes[(64 + small.Length)..(int)largeEntry.DataOffset], b => Assert.Equal(0, b));
        Assert.Equal(large, reader.ReadAsset(largeEntry));
        Assert.Equal(1, OapAlignment.None.For(1_000_000));
    }

    /// <summary>Blobs keep the order they were added in, while the index stays sorted by id.</summary>
    [Fact]
    public void BlobOrder_FollowsAddOrder()
    {
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var writer = new OapWriter();
        foreach (var id in ids) writer.Add(id, Encoding.UTF8.GetBytes(id.ToString()));
        var reader = OapReader.Open(writer.Serialize());

        var offsets = ids.Select(id => reader.FindById(id)!.Value.DataOffset).ToArray();
        Assert.Equal(offsets.Order(), offsets);
        Assert.True(reader.IsSortedIndex);
    }

    /// <summary>Writing after other bytes in a stream pads relative to the package start, not the stream.</summary>
    [Fact]
    public void WriteTo_PadsRelativeToThePackageStart()
    {
        var data = RandomNumberGenerator.GetBytes(70_000);
        var writer = new OapWriter { Alignment = OapAlignment.Default };
        writer.Add(Guid.NewGuid(), data, compression: OapCompressChoice.Fixed(OapCompression.Store));
        using var stream = new MemoryStream();
        stream.Write(new byte[7]);

        writer.WriteTo(stream);

        var reader = OapReader.Open(stream.ToArray()[7..]);
        Assert.Equal(data, reader.ReadAsset(reader.EntryAt(0)));
    }

    /// <summary>A fixed Store choice stores compressible data instead of being taken for the automatic default.</summary>
    [Fact]
    public void FixedStore_IsNotTreatedAsAuto()
    {
        var data = Encoding.UTF8.GetBytes(new string('a', 4096));
        var writer = new OapWriter();
        writer.Add(Guid.NewGuid(), data, compression: OapCompressChoice.Fixed(OapCompression.Store));
        writer.Add(Guid.NewGuid(), data);
        var reader = OapReader.Open(writer.Serialize());

        Assert.Contains(reader.Entries, e => e.Compression == OapCompression.Store);
        Assert.Contains(reader.Entries, e => e.Compression == OapCompression.Deflate);
        Assert.True(default(OapCompressChoice).IsAuto);
        Assert.False(OapCompressChoice.Fixed(OapCompression.Store).IsAuto);
    }

    /// <summary>The package hash is stable for identical builds and changes with paths and manifests.</summary>
    [Fact]
    public void PackageHash_TracksMetadata()
    {
        var id = Guid.NewGuid();
        string Hash(string path, string manifest)
        {
            var writer = new OapWriter();
            writer.Add(id, "same bytes"u8, path);
            writer.SetManifest(manifest);
            return OapReader.Open(writer.Serialize()).PackageHash;
        }

        var hash = Hash("a/b.txt", "{}");
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, Hash("a/b.txt", "{}"));
        Assert.NotEqual(hash, Hash("a/c.txt", "{}"));
        Assert.NotEqual(hash, Hash("a/b.txt", "{\"v\":2}"));
    }

    /// <summary>An index outside the package, a size that contradicts the entry count, or a blob past the end fail.</summary>
    [Fact]
    public void Index_InconsistentLayoutsAreRejected()
    {
        var writer = new OapWriter();
        writer.Add(Guid.NewGuid(), "payload"u8);
        var good = writer.Serialize();

        byte[] WithHeader(Action<byte[]> change)
        {
            var bytes = good.ToArray();
            change(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), OapFormat.Crc32(bytes.AsSpan(0, 60)));
            return bytes;
        }

        Assert.Throws<OapTruncatedException>(() => OapReader.Open(WithHeader(b => b[16] = 1)));
        Assert.Throws<OapCorruptDataException>(() => OapReader.Open(WithHeader(b => b[24] = 63)));
        var index = (int)OapReader.Open(good).Header.IndexOffset;
        var blobPastEnd = good.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(blobPastEnd.AsSpan(index + 24), 1 << 20);
        Assert.Throws<OapTruncatedException>(() => OapReader.Open(blobPastEnd));
    }

    /// <summary>The header records format 1.1 and readers accept any minor version of major 1.</summary>
    [Fact]
    public void Header_RecordsMinorVersion()
    {
        var bytes = new OapWriter().Serialize();
        Assert.Equal(1, OapReader.Open(bytes).Header.Minor);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), OapFormat.Crc32(bytes.AsSpan(0, 60)));
        Assert.Equal(9, OapReader.Open(bytes).Header.Minor);
    }
}
