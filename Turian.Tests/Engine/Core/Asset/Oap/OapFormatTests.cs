namespace Turian.Tests;

/// <summary>Tests for the OAP header/index byte layout and CRC-32 primitive.</summary>
public class OapFormatTests
{
    /// <summary>The CRC-32 primitive matches the canonical ISO-HDLC check value.</summary>
    [Fact]
    public void Crc32_MatchesCanonicalCheckValue()
    {
        var value = OapFormat.Crc32("123456789"u8);
        Assert.Equal(0xCBF43926u, value);
    }

    /// <summary>An asset id round-trips through the 16-byte big-endian encoding.</summary>
    [Fact]
    public void AssetId_RoundTripsBigEndian()
    {
        var id = Guid.NewGuid();
        Span<byte> buffer = stackalloc byte[OapFormat.AssetIdSize];
        OapFormat.WriteAssetId(id, buffer);

        Assert.Equal(id, OapFormat.ReadAssetId(buffer));
        // Byte 0 of the big-endian form is the top byte of the first Guid field.
        Assert.Equal(id.ToByteArray(bigEndian: true)[0], buffer[0]);
    }

    /// <summary>The header serialises and parses back to an equal value.</summary>
    [Fact]
    public void Header_RoundTrips()
    {
        var header = new OapHeader
        {
            Flags = OapFlags.SortedIndex | OapFlags.HasManifest,
            EntryCount = 3,
            IndexOffset = 1024,
            IndexSize = 192,
            StringTableOffset = 800,
            StringTableSize = 64,
            ManifestOffset = 900,
            ManifestSize = 40
        };

        var buffer = new byte[OapFormat.HeaderSize];
        header.Write(buffer);

        Assert.Equal(header, OapHeader.Read(buffer));
    }

    /// <summary>A wrong magic is rejected with <see cref="OapBadMagicException"/>.</summary>
    [Fact]
    public void Header_RejectsBadMagic()
    {
        var buffer = new byte[OapFormat.HeaderSize];
        new OapHeader().Write(buffer);
        buffer[0] = (byte)'X';

        Assert.Throws<OapBadMagicException>(() => OapHeader.Read(buffer));
    }

    /// <summary>A flipped byte under the header CRC is rejected as corrupt.</summary>
    [Fact]
    public void Header_RejectsCorruption()
    {
        var buffer = new byte[OapFormat.HeaderSize];
        new OapHeader { EntryCount = 7 }.Write(buffer);
        buffer[12] ^= 0xFF;

        Assert.Throws<OapCorruptDataException>(() => OapHeader.Read(buffer));
    }

    /// <summary>A future major version is rejected.</summary>
    [Fact]
    public void Header_RejectsFutureMajorVersion()
    {
        var buffer = new byte[OapFormat.HeaderSize];
        new OapHeader().Write(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), 2);
        // Recompute the CRC so only the version triggers the rejection.
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(60),
            OapFormat.Crc32(buffer.AsSpan(0, OapFormat.HeaderCrcCoverage)));

        Assert.Throws<OapUnsupportedVersionException>(() => OapHeader.Read(buffer));
    }

    /// <summary>An index entry serialises and parses back to an equal value.</summary>
    [Fact]
    public void IndexEntry_RoundTrips()
    {
        var entry = new OapIndexEntry
        {
            AssetId = Guid.NewGuid(),
            DataOffset = 64,
            StoredSize = 100,
            UncompressedSize = 250,
            ContentCrc32 = 0xDEADBEEF,
            Compression = OapCompression.Deflate,
            AssetType = 7,
            Encryption = OapEncryption.ChaCha20,
            VirtualPathOffset = 2048,
            VirtualPathLength = 17,
            DependencyCount = 2,
            DependencyOffset = 4096
        };

        var buffer = new byte[OapFormat.IndexEntrySize];
        entry.Write(buffer);

        Assert.Equal(entry, OapIndexEntry.Read(buffer));
    }
}
