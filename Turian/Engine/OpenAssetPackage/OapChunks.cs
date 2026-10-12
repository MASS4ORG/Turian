namespace OpenAssetPackage;

/// <summary>
/// The block table of a chunked blob (spec §5.3): the plaintext split into fixed-size blocks, each compressed on
/// its own, so any plaintext range decodes from only the blocks covering it.
/// </summary>
public sealed class OapBlockTable
{
    /// <summary>Bytes before the block table: <c>block_size</c> and <c>block_count</c>.</summary>
    public const int PrefixSize = 8;

    /// <summary>Bytes of one block table entry: <c>stored_size</c> and <c>block_crc32</c>.</summary>
    public const int EntrySize = 8;

    /// <summary>The smallest block size the format allows.</summary>
    public const int MinBlockSize = 4096;

    /// <summary>The largest block size the format allows.</summary>
    public const int MaxBlockSize = 16 * 1024 * 1024;

    readonly uint[] storedSizes;
    readonly uint[] crcs;
    readonly long[] offsets;

    OapBlockTable(int blockSize, long uncompressedSize, uint[] storedSizes, uint[] crcs)
    {
        BlockSize = blockSize;
        UncompressedSize = uncompressedSize;
        this.storedSizes = storedSizes;
        this.crcs = crcs;
        offsets = new long[storedSizes.Length];
        var at = (long)PrefixSize + (long)EntrySize * storedSizes.Length;
        for (var i = 0; i < storedSizes.Length; i++)
        {
            offsets[i] = at;
            at += storedSizes[i];
        }

        StoredSize = at;
    }

    /// <summary>Gets the plaintext length of every block but the last.</summary>
    public int BlockSize { get; }

    /// <summary>Gets the number of blocks.</summary>
    public int BlockCount => storedSizes.Length;

    /// <summary>Gets the plaintext length of the whole asset.</summary>
    public long UncompressedSize { get; }

    /// <summary>Gets the stored length of the whole blob: prefix, table and blocks.</summary>
    public long StoredSize { get; }

    /// <summary>Gets the offset of a block's stored bytes from the start of the blob.</summary>
    /// <param name="block">The block index.</param>
    /// <returns>The offset in bytes.</returns>
    public long StoredOffset(int block) => offsets[block];

    /// <summary>Gets the stored length of a block.</summary>
    /// <param name="block">The block index.</param>
    /// <returns>The length in bytes.</returns>
    public int StoredLength(int block) => (int)storedSizes[block];

    /// <summary>Gets the CRC-32 of a block's plaintext.</summary>
    /// <param name="block">The block index.</param>
    /// <returns>The checksum.</returns>
    public uint Crc32(int block) => crcs[block];

    /// <summary>Gets the plaintext length of a block: <see cref="BlockSize"/>, or less for the last block.</summary>
    /// <param name="block">The block index.</param>
    /// <returns>The length in bytes.</returns>
    public int PlainLength(int block) => (int)Math.Min(BlockSize, UncompressedSize - (long)block * BlockSize);

    /// <summary>Gets whether a block is stored raw because compressing it did not make it smaller.</summary>
    /// <param name="block">The block index.</param>
    /// <returns><see langword="true"/> when the stored bytes are the plaintext.</returns>
    public bool IsRaw(int block) => storedSizes[block] == PlainLength(block);

    /// <summary>Reads the 8-byte prefix: the block size and the byte length of the table that follows.</summary>
    /// <param name="prefix">The first <see cref="PrefixSize"/> bytes of the blob.</param>
    /// <param name="uncompressedSize">The entry's plaintext length.</param>
    /// <returns>The block size and the table length in bytes.</returns>
    /// <exception cref="OapCorruptDataException">The prefix contradicts the spec or the entry.</exception>
    public static (int BlockSize, int TableLength) ReadPrefix(ReadOnlySpan<byte> prefix, ulong uncompressedSize)
    {
        if (prefix.Length < PrefixSize) throw new OapTruncatedException("OAP chunked blob prefix is truncated.");
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);
        if (blockSize is < MinBlockSize or > MaxBlockSize || !BitOperations.IsPow2(blockSize))
            throw new OapCorruptDataException($"OAP chunked blob has an invalid block size {blockSize}.");
        if (uncompressedSize == 0 || count != (uncompressedSize + blockSize - 1) / blockSize)
            throw new OapCorruptDataException("OAP chunked blob block count does not match its plaintext length.");
        return ((int)blockSize, checked((int)count * EntrySize));
    }

    /// <summary>Parses the block table that follows the prefix and checks it against the entry's stored size.</summary>
    /// <param name="blockSize">The block size from <see cref="ReadPrefix"/>.</param>
    /// <param name="table">The table bytes.</param>
    /// <param name="uncompressedSize">The entry's plaintext length.</param>
    /// <param name="storedSize">The entry's stored length.</param>
    /// <returns>The block table.</returns>
    /// <exception cref="OapCorruptDataException">The block sizes do not add up to the stored length.</exception>
    public static OapBlockTable Parse(int blockSize, ReadOnlySpan<byte> table, ulong uncompressedSize,
        ulong storedSize)
    {
        var count = table.Length / EntrySize;
        var sizes = new uint[count];
        var crcs = new uint[count];
        for (var i = 0; i < count; i++)
        {
            sizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(table[(i * EntrySize)..]);
            crcs[i] = BinaryPrimitives.ReadUInt32LittleEndian(table[(i * EntrySize + 4)..]);
        }

        var result = new OapBlockTable(blockSize, (long)uncompressedSize, sizes, crcs);
        if ((ulong)result.StoredSize != storedSize)
            throw new OapCorruptDataException("OAP chunked blob block sizes do not add up to its stored size.");
        for (var i = 0; i < count; i++)
            if (sizes[i] == 0 || sizes[i] > result.PlainLength(i))
                throw new OapCorruptDataException($"OAP chunked blob block {i} has an invalid stored size.");
        return result;
    }

    /// <summary>Parses the prefix and table at the start of a whole stored (decrypted) blob.</summary>
    /// <param name="blob">The stored blob.</param>
    /// <param name="uncompressedSize">The entry's plaintext length.</param>
    /// <returns>The block table.</returns>
    public static OapBlockTable Parse(ReadOnlySpan<byte> blob, ulong uncompressedSize)
    {
        var (blockSize, tableLength) = ReadPrefix(blob, uncompressedSize);
        if (blob.Length < PrefixSize + tableLength) throw new OapTruncatedException("OAP block table is truncated.");
        return Parse(blockSize, blob.Slice(PrefixSize, tableLength), uncompressedSize, (ulong)blob.Length);
    }
}

/// <summary>Encodes and decodes chunked blobs (spec §5.3).</summary>
public static class OapChunks
{
    /// <summary>Splits <paramref name="data"/> into blocks and compresses each, storing it raw unless smaller.</summary>
    /// <param name="data">The plaintext; must not be empty.</param>
    /// <param name="codec">The per-block codec, DEFLATE or Zstandard.</param>
    /// <param name="blockSize">A power of two between 4&#160;KiB and 16&#160;MiB.</param>
    /// <param name="level">The codec level, or 0 for the codec's default.</param>
    /// <returns>The stored blob: prefix, block table and blocks.</returns>
    public static byte[] Encode(ReadOnlySpan<byte> data, OapCompression codec, int blockSize, int level = 0)
    {
        if (data.IsEmpty) throw new ArgumentException("An empty asset cannot be chunked.", nameof(data));
        ValidateEncode(codec, blockSize);
        var count = (data.Length + blockSize - 1) / blockSize;
        var blocks = new byte[count][];
        var header = new byte[OapBlockTable.PrefixSize + count * OapBlockTable.EntrySize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)count);
        for (var i = 0; i < count; i++)
        {
            var plain = data.Slice(i * blockSize, Math.Min(blockSize, data.Length - i * blockSize));
            var packed = OapCompressionCodec.Compress(codec, plain, level);
            blocks[i] = packed.Length < plain.Length ? packed : plain.ToArray();
            var entry = header.AsSpan(OapBlockTable.PrefixSize + i * OapBlockTable.EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)blocks[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], OapFormat.Crc32(plain));
        }

        return Concat(header, blocks);
    }

    static void ValidateEncode(OapCompression codec, int blockSize)
    {
        if (codec == OapCompression.Store)
            throw new ArgumentException("Chunking needs a compressing codec.", nameof(codec));
        if (blockSize is < OapBlockTable.MinBlockSize or > OapBlockTable.MaxBlockSize
            || !BitOperations.IsPow2(blockSize))
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                "Block size must be a power of two, 4 KiB to 16 MiB.");
    }

    static byte[] Concat(byte[] header, byte[][] blocks)
    {
        var result = new byte[header.Length + blocks.Sum(static b => (long)b.Length)];
        header.CopyTo(result, 0);
        var at = header.Length;
        foreach (var block in blocks)
        {
            block.CopyTo(result, at);
            at += block.Length;
        }

        return result;
    }

    /// <summary>Decodes one block into <paramref name="destination"/>, checking its CRC-32 when asked.</summary>
    /// <param name="table">The blob's block table.</param>
    /// <param name="block">The block index.</param>
    /// <param name="stored">The block's stored (decrypted) bytes.</param>
    /// <param name="codec">The entry's codec.</param>
    /// <param name="destination">Receives exactly <see cref="OapBlockTable.PlainLength"/> bytes.</param>
    /// <param name="verify">Whether to check the block's CRC-32.</param>
    /// <exception cref="OapCorruptDataException">The block failed to decode or its checksum did not match.</exception>
    public static void DecodeBlock(OapBlockTable table, int block, ReadOnlySpan<byte> stored, OapCompression codec,
        Span<byte> destination, bool verify)
    {
        if (table.IsRaw(block)) stored.CopyTo(destination);
        else OapCompressionCodec.DecompressInto(codec, stored, destination[..table.PlainLength(block)]);

        if (verify && OapFormat.Crc32(destination[..table.PlainLength(block)]) != table.Crc32(block))
            throw new OapCorruptDataException($"OAP chunked blob block {block} failed its CRC-32 check.");
    }

    /// <summary>Decodes a whole stored (decrypted) chunked blob.</summary>
    /// <param name="blob">The stored blob.</param>
    /// <param name="codec">The entry's codec.</param>
    /// <param name="uncompressedSize">The entry's plaintext length.</param>
    /// <param name="verify">Whether to check every block's CRC-32.</param>
    /// <returns>The plaintext.</returns>
    public static byte[] Decode(ReadOnlySpan<byte> blob, OapCompression codec, ulong uncompressedSize, bool verify)
    {
        var table = OapBlockTable.Parse(blob, uncompressedSize);
        var result = new byte[checked((int)uncompressedSize)];
        for (var i = 0; i < table.BlockCount; i++)
        {
            var stored = blob.Slice((int)table.StoredOffset(i), table.StoredLength(i));
            DecodeBlock(table, i, stored, codec, result.AsSpan(i * table.BlockSize), verify);
        }

        return result;
    }
}
