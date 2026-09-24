namespace Turian.Engine.Core;

/// <summary>
/// On-disk constants and structures for the Open Asset Package (<c>.oap</c>) container
/// format, version 1.0. This type is the single source of truth for the byte-level
/// layout described in the OAP specification; it performs no I/O.
/// </summary>
/// <remarks>
/// All multi-byte integers are little-endian. Offsets and sizes are absolute byte
/// counts from the start of the file. See <see cref="OapWriter"/> and
/// <see cref="OapReader"/> for the code that produces and consumes these structures.
/// </remarks>
public static class OapFormat
{
    /// <summary>File extension of an Open Asset Package.</summary>
    public const string FileExtension = ".oap";

    /// <summary>Header magic at offset 0: the ASCII bytes <c>OAP</c> followed by <c>0x01</c>.</summary>
    public static ReadOnlySpan<byte> Magic => [0x4F, 0x41, 0x50, 0x01];

    /// <summary>Major format version emitted and accepted by this implementation.</summary>
    public const ushort FormatMajor = 1;

    /// <summary>Minor format version emitted by this implementation.</summary>
    public const ushort FormatMinor = 0;

    /// <summary>Fixed size of the file header, in bytes.</summary>
    public const int HeaderSize = 64;

    /// <summary>Fixed size of a single index entry, in bytes.</summary>
    public const int IndexEntrySize = 64;

    /// <summary>Size of a 128-bit asset identifier, in bytes.</summary>
    public const int AssetIdSize = 16;

    /// <summary>Number of leading header bytes covered by the header CRC-32.</summary>
    public const int HeaderCrcCoverage = 60;

    /// <summary>
    /// Maps a <see cref="Guid"/> to the 16 raw bytes stored in an index entry, using
    /// RFC&#160;4122 (big-endian) field order so the lexicographic index sort matches the
    /// canonical UUID string order and is independent of the host's endianness.
    /// </summary>
    /// <param name="id">The asset identifier.</param>
    /// <param name="destination">A span of at least <see cref="AssetIdSize"/> bytes.</param>
    public static void WriteAssetId(Guid id, Span<byte> destination)
    {
        if (!id.TryWriteBytes(destination[..AssetIdSize], bigEndian: true, out _))
        {
            throw new ArgumentException("Destination span is too small for an asset id.", nameof(destination));
        }
    }

    /// <summary>Reads a <see cref="Guid"/> from the 16 raw bytes of an index entry.</summary>
    /// <param name="source">A span of at least <see cref="AssetIdSize"/> bytes.</param>
    /// <returns>The decoded asset identifier.</returns>
    public static Guid ReadAssetId(ReadOnlySpan<byte> source) => new(source[..AssetIdSize], bigEndian: true);

    /// <summary>Computes the CRC-32/ISO-HDLC checksum used throughout the format.</summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <returns>The CRC-32 value.</returns>
    public static uint Crc32(ReadOnlySpan<byte> data) => System.IO.Hashing.Crc32.HashToUInt32(data);
}

/// <summary>Compression codec applied to a single asset blob (OAP spec §5.1).</summary>
public enum OapCompression : byte
{
    /// <summary>No compression; the stored bytes are the plaintext.</summary>
    Store = 0,

    /// <summary>Raw DEFLATE stream (RFC&#160;1951), with no zlib or gzip wrapper.</summary>
    Deflate = 1,

    /// <summary>Reserved for Zstandard. Not emitted by a v1 writer; rejected on read.</summary>
    Zstd = 2
}

/// <summary>Encryption codec applied to an already-compressed asset blob (OAP spec §5.2).</summary>
public enum OapEncryption : byte
{
    /// <summary>Not encrypted.</summary>
    None = 0,

    /// <summary>Lightweight keyed-XOR obfuscation. Trivially breakable.</summary>
    Xor = 1,

    /// <summary>ChaCha20 (IETF, RFC&#160;8439) with a 256-bit key.</summary>
    ChaCha20 = 2
}

/// <summary>Header flag bits (OAP spec §3.1).</summary>
[Flags]
public enum OapFlags : uint
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>Index entries are sorted ascending by <c>asset_id</c>, enabling binary search.</summary>
    SortedIndex = 1 << 0,

    /// <summary>A manifest is present.</summary>
    HasManifest = 1 << 1,

    /// <summary>At least one asset is encrypted. Informational only.</summary>
    Encrypted = 1 << 2
}

/// <summary>In-memory view of the 64-byte OAP file header.</summary>
public readonly record struct OapHeader
{
    /// <summary>Gets the header flag bits.</summary>
    public OapFlags Flags { get; init; }

    /// <summary>Gets the number of index entries.</summary>
    public uint EntryCount { get; init; }

    /// <summary>Gets the absolute offset of the index.</summary>
    public ulong IndexOffset { get; init; }

    /// <summary>Gets the index size in bytes (<see cref="EntryCount"/> × 64).</summary>
    public ulong IndexSize { get; init; }

    /// <summary>Gets the absolute offset of the string table.</summary>
    public ulong StringTableOffset { get; init; }

    /// <summary>Gets the string table size in bytes.</summary>
    public ulong StringTableSize { get; init; }

    /// <summary>Gets the absolute offset of the manifest, or 0 when absent.</summary>
    public ulong ManifestOffset { get; init; }

    /// <summary>Gets the manifest size in bytes, or 0 when absent.</summary>
    public uint ManifestSize { get; init; }

    /// <summary>
    /// Serialises this header into a 64-byte buffer, computing and appending
    /// <c>header_crc32</c> over the first 60 bytes.
    /// </summary>
    /// <param name="destination">A span of at least <see cref="OapFormat.HeaderSize"/> bytes.</param>
    public void Write(Span<byte> destination)
    {
        var buffer = destination[..OapFormat.HeaderSize];
        buffer.Clear();

        OapFormat.Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..], OapFormat.FormatMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], OapFormat.FormatMinor);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], (uint)Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], EntryCount);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[16..], IndexOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[24..], IndexSize);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[32..], StringTableOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[40..], StringTableSize);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[48..], ManifestOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[56..], ManifestSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer[60..],
            OapFormat.Crc32(buffer[..OapFormat.HeaderCrcCoverage]));
    }

    /// <summary>Parses and validates a header from the start of <paramref name="data"/>.</summary>
    /// <param name="data">A span of at least <see cref="OapFormat.HeaderSize"/> bytes.</param>
    /// <returns>The parsed header.</returns>
    /// <exception cref="OapTruncatedException">The span is shorter than a header.</exception>
    /// <exception cref="OapBadMagicException">The magic bytes do not match.</exception>
    /// <exception cref="OapUnsupportedVersionException">The major version is newer than supported.</exception>
    /// <exception cref="OapCorruptDataException">The header CRC-32 check failed.</exception>
    public static OapHeader Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < OapFormat.HeaderSize)
        {
            throw new OapTruncatedException("OAP data is shorter than its 64-byte header.");
        }

        if (!data[..4].SequenceEqual(OapFormat.Magic))
        {
            throw new OapBadMagicException();
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(data[4..6]);
        if (major > OapFormat.FormatMajor)
        {
            throw new OapUnsupportedVersionException(major);
        }

        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data[60..64]);
        if (OapFormat.Crc32(data[..OapFormat.HeaderCrcCoverage]) != storedCrc)
        {
            throw new OapCorruptDataException("OAP header CRC-32 mismatch.");
        }

        return new OapHeader
        {
            Flags = (OapFlags)BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]),
            EntryCount = BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]),
            IndexOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[16..24]),
            IndexSize = BinaryPrimitives.ReadUInt64LittleEndian(data[24..32]),
            StringTableOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[32..40]),
            StringTableSize = BinaryPrimitives.ReadUInt64LittleEndian(data[40..48]),
            ManifestOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[48..56]),
            ManifestSize = BinaryPrimitives.ReadUInt32LittleEndian(data[56..60])
        };
    }
}

/// <summary>In-memory view of a 64-byte OAP index entry.</summary>
public readonly record struct OapIndexEntry
{
    /// <summary>Gets the 128-bit stable asset identifier (the primary key).</summary>
    public Guid AssetId { get; init; }

    /// <summary>Gets the absolute offset of the stored (possibly compressed and encrypted) blob.</summary>
    public ulong DataOffset { get; init; }

    /// <summary>Gets the blob size on disk, in bytes.</summary>
    public ulong StoredSize { get; init; }

    /// <summary>Gets the size of the asset after decompression, in bytes.</summary>
    public ulong UncompressedSize { get; init; }

    /// <summary>Gets the CRC-32 of the uncompressed, unencrypted asset bytes.</summary>
    public uint ContentCrc32 { get; init; }

    /// <summary>Gets the compression codec applied to the plaintext.</summary>
    public OapCompression Compression { get; init; }

    /// <summary>Gets the application-defined category byte (0 = unknown).</summary>
    public byte AssetType { get; init; }

    /// <summary>Gets the encryption codec applied to the compressed blob.</summary>
    public OapEncryption Encryption { get; init; }

    /// <summary>Gets the offset into the string table of this entry's virtual path.</summary>
    public uint VirtualPathOffset { get; init; }

    /// <summary>Gets the length in bytes of this entry's virtual path.</summary>
    public ushort VirtualPathLength { get; init; }

    /// <summary>Gets the number of dependency ids declared by this entry.</summary>
    public ushort DependencyCount { get; init; }

    /// <summary>Gets the absolute offset of this entry's dependency list, or 0 when it has none.</summary>
    public ulong DependencyOffset { get; init; }

    /// <summary>Serialises this entry into a 64-byte buffer.</summary>
    /// <param name="destination">A span of at least <see cref="OapFormat.IndexEntrySize"/> bytes.</param>
    public void Write(Span<byte> destination)
    {
        var buffer = destination[..OapFormat.IndexEntrySize];
        buffer.Clear();

        OapFormat.WriteAssetId(AssetId, buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[16..], DataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[24..], StoredSize);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[32..], UncompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[40..], ContentCrc32);
        buffer[44] = (byte)Compression;
        buffer[45] = AssetType;
        buffer[46] = (byte)Encryption;
        buffer[47] = 0; // entry_flags, reserved
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[48..], VirtualPathOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[52..], VirtualPathLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[54..], DependencyCount);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[56..], DependencyOffset);
    }

    /// <summary>Parses an index entry from the start of <paramref name="data"/>.</summary>
    /// <param name="data">A span of at least <see cref="OapFormat.IndexEntrySize"/> bytes.</param>
    /// <returns>The parsed entry.</returns>
    /// <exception cref="OapTruncatedException">The span is shorter than an index entry.</exception>
    public static OapIndexEntry Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < OapFormat.IndexEntrySize)
        {
            throw new OapTruncatedException("OAP index entry is truncated.");
        }

        return new OapIndexEntry
        {
            AssetId = OapFormat.ReadAssetId(data),
            DataOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[16..24]),
            StoredSize = BinaryPrimitives.ReadUInt64LittleEndian(data[24..32]),
            UncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(data[32..40]),
            ContentCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(data[40..44]),
            Compression = (OapCompression)data[44],
            AssetType = data[45],
            Encryption = (OapEncryption)data[46],
            VirtualPathOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[48..52]),
            VirtualPathLength = BinaryPrimitives.ReadUInt16LittleEndian(data[52..54]),
            DependencyCount = BinaryPrimitives.ReadUInt16LittleEndian(data[54..56]),
            DependencyOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[56..64])
        };
    }
}
