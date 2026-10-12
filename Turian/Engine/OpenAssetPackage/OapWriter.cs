namespace OpenAssetPackage;

/// <summary>
/// How a newly added asset is compressed. The default value is <see cref="Auto"/>.
/// </summary>
public readonly record struct OapCompressChoice
{
    // Inverted so the default value is automatic and a fixed Store choice differs from it.
    bool IsFixed { get; init; }

    /// <summary>Gets a value indicating whether storing wins when <see cref="Codec"/> is not smaller.</summary>
    public bool IsAuto => !IsFixed;

    /// <summary>Gets the codec to apply, or to try against storing when <see cref="IsAuto"/>.</summary>
    public OapCompression Codec { get; init; }

    /// <summary>Gets the codec level, or 0 for the codec's default.</summary>
    public int Level { get; init; }

    /// <summary>Gets the block size of a chunked blob (spec §5.3), or 0 to compress the asset whole.</summary>
    public int BlockSize { get; init; }

    /// <summary>Gets the 1.0-compatible default: the smaller of store and DEFLATE.</summary>
    public static OapCompressChoice Auto => new() { Codec = OapCompression.Deflate };

    /// <summary>Picks the smaller of storing and <paramref name="codec"/>, never inflating an asset.</summary>
    /// <param name="codec">The codec to try.</param>
    /// <param name="level">The codec level, or 0 for the codec's default.</param>
    /// <returns>An automatic choice.</returns>
    public static OapCompressChoice Smallest(OapCompression codec, int level = 0) =>
        new() { Codec = codec, Level = level };

    /// <summary>Forces a specific codec.</summary>
    /// <param name="codec">The codec to use for every asset.</param>
    /// <returns>A fixed-codec choice.</returns>
    public static OapCompressChoice Fixed(OapCompression codec) => new() { IsFixed = true, Codec = codec };

    /// <summary>Splits assets into independently compressed blocks of <paramref name="blockSize"/> bytes.</summary>
    /// <param name="codec">The per-block codec.</param>
    /// <param name="blockSize">A power of two between 4&#160;KiB and 16&#160;MiB.</param>
    /// <param name="level">The codec level, or 0 for the codec's default.</param>
    /// <returns>A chunked choice.</returns>
    public static OapCompressChoice Chunked(OapCompression codec, int blockSize, int level = 0) =>
        new() { IsFixed = true, Codec = codec, Level = level, BlockSize = blockSize };
}

/// <summary>
/// Where blobs start in the data section. Large blobs aligned to a page or sector can be memory mapped or read
/// with direct I/O; small ones stay packed. Padding is zero and never referenced (spec §2).
/// </summary>
/// <param name="Small">The alignment of blobs below <paramref name="LargeThreshold"/>; 1 packs them.</param>
/// <param name="Large">The alignment of blobs of at least <paramref name="LargeThreshold"/> bytes.</param>
/// <param name="LargeThreshold">The stored size from which a blob counts as large.</param>
public readonly record struct OapAlignment(int Small, int Large, int LargeThreshold)
{
    /// <summary>Gets the packed layout with no padding, as 1.0 writers emit.</summary>
    public static OapAlignment None => new(1, 1, int.MaxValue);

    /// <summary>Gets 4&#160;KiB alignment for blobs of 64&#160;KiB and more, 16 bytes below.</summary>
    public static OapAlignment Default => new(16, 4096, 64 * 1024);

    /// <summary>Gets the alignment of a blob of <paramref name="storedSize"/> bytes.</summary>
    /// <param name="storedSize">The blob's stored size.</param>
    /// <returns>The alignment in bytes.</returns>
    public int For(long storedSize) => Math.Max(1, storedSize >= LargeThreshold ? Large : Small);
}

/// <summary>
/// Builds an Open Asset Package. Assets are added one at a time (compressed and encrypted eagerly), then
/// <see cref="WriteTo"/> streams the file out. Blobs are written in the order they were added, so callers place
/// assets that load together next to each other; index entries are sorted by <c>asset_id</c> for binary search.
/// </summary>
public sealed class OapWriter
{
    readonly List<PendingAsset> assets = [];
    byte[]? manifest;
    byte[]? key;

    /// <summary>Gets the number of entries added so far, tombstones included.</summary>
    public int Count => assets.Count;

    /// <summary>Gets or sets where blobs start in the data section.</summary>
    public OapAlignment Alignment { get; set; } = OapAlignment.None;

    /// <summary>
    /// Sets the encryption key applied to assets added with a non-<see cref="OapEncryption.None"/>
    /// codec. Derive one from a passphrase with <see cref="OapCrypto.DeriveKey"/>.
    /// </summary>
    /// <param name="encryptionKey">A 32-byte key.</param>
    public void SetKey(ReadOnlySpan<byte> encryptionKey)
    {
        if (encryptionKey.Length != OapCrypto.KeyLength)
        {
            throw new ArgumentException($"An OAP key must be {OapCrypto.KeyLength} bytes.", nameof(encryptionKey));
        }

        key = [.. encryptionKey];
    }

    /// <summary>Sets the package manifest (opaque bytes, UTF-8 JSON by convention). The bytes are copied.</summary>
    /// <param name="bytes">The manifest bytes.</param>
    public void SetManifest(ReadOnlySpan<byte> bytes) => manifest = [.. bytes];

    /// <summary>Sets the package manifest from a UTF-8 string.</summary>
    /// <param name="json">The manifest text.</param>
    public void SetManifest(string json) => SetManifest(Encoding.UTF8.GetBytes(json));

    /// <summary>
    /// Adds an asset. Compresses and encrypts immediately and copies every input, so
    /// the caller's buffers can be reused right after.
    /// </summary>
    /// <param name="id">The 128-bit asset identifier (primary key).</param>
    /// <param name="data">The plaintext asset bytes.</param>
    /// <param name="virtualPath">A human-readable path such as <c>textures/hero.ktx2</c>. May be empty.</param>
    /// <param name="assetType">An application-defined category byte (0 = unknown).</param>
    /// <param name="compression">How to compress the asset.</param>
    /// <param name="encryption">The encryption codec; requires a key set via <see cref="SetKey"/>.</param>
    /// <param name="dependencies">Ids of assets this one depends on. Copied.</param>
    public void Add(
        Guid id,
        ReadOnlySpan<byte> data,
        string virtualPath = "",
        byte assetType = 0,
        OapCompressChoice compression = default,
        OapEncryption encryption = OapEncryption.None,
        IReadOnlyList<Guid>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(virtualPath);

        var crc = OapFormat.Crc32(data);
        var (codec, chunked, stored) = Encode(data, compression == default ? OapCompressChoice.Auto : compression);

        if (encryption != OapEncryption.None)
        {
            if (key is null)
            {
                throw new OapKeyRequiredException();
            }

            Span<byte> idBytes = stackalloc byte[OapFormat.AssetIdSize];
            OapFormat.WriteAssetId(id, idBytes);
            OapCrypto.Apply(encryption, key, idBytes, crc, stored);
        }

        assets.Add(new PendingAsset
        {
            AssetId = id,
            VirtualPath = Encoding.UTF8.GetBytes(virtualPath),
            AssetType = assetType,
            Compression = codec,
            IsChunked = chunked,
            Encryption = encryption,
            UncompressedSize = (ulong)data.Length,
            ContentCrc32 = crc,
            Stored = stored,
            Dependencies = dependencies is null ? [] : [.. dependencies]
        });
    }

    /// <summary>
    /// Adds a tombstone: in a layered mount, the asset is removed from every package below this one (spec §4.3).
    /// </summary>
    /// <param name="id">The id of the asset to remove.</param>
    /// <param name="virtualPath">The path to remove with it, or empty.</param>
    public void AddTombstone(Guid id, string virtualPath = "")
    {
        ArgumentNullException.ThrowIfNull(virtualPath);
        assets.Add(new PendingAsset
        {
            AssetId = id,
            VirtualPath = Encoding.UTF8.GetBytes(virtualPath),
            Flags = OapEntryFlags.Tombstone
        });
    }

    static (OapCompression Codec, bool Chunked, byte[] Stored) Encode(ReadOnlySpan<byte> data,
        OapCompressChoice choice)
    {
        if (choice.BlockSize > 0 && !data.IsEmpty && choice.Codec != OapCompression.Store)
            return (choice.Codec, true, OapChunks.Encode(data, choice.Codec, choice.BlockSize, choice.Level));
        if (!choice.IsAuto)
            return (choice.Codec, false, OapCompressionCodec.Compress(choice.Codec, data, choice.Level));

        var (codec, stored) = OapCompressionCodec.Best(data, choice.Codec, choice.Level);
        return (codec, false, stored);
    }

    /// <summary>Lays the whole package out into a single freshly allocated array.</summary>
    /// <returns>The complete <c>.oap</c> bytes.</returns>
    public byte[] Serialize()
    {
        using var buffer = new MemoryStream();
        WriteTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Streams the package to <paramref name="output"/> from its current position, writing each blob once: the
    /// layout is computed up front, so no section is buffered or patched afterwards.
    /// </summary>
    /// <param name="output">A writable stream.</param>
    public void WriteTo(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var layout = Layout();
        var writer = new PaddedWriter(output);

        Span<byte> headerBytes = stackalloc byte[OapFormat.HeaderSize];
        layout.Header.Write(headerBytes);
        writer.Write(headerBytes);

        foreach (var asset in assets)
        {
            if (asset.Stored.Length == 0) continue;
            writer.PadTo(layout.DataOffsets[asset]);
            writer.Write(asset.Stored);
        }

        WriteDependencies(writer, layout.Ordered);
        foreach (var asset in layout.Ordered) writer.Write(asset.VirtualPath);
        if (manifest is { Length: > 0 }) writer.Write(manifest);
        WriteIndex(writer, layout.Index);
    }

    static void WriteDependencies(PaddedWriter writer, List<PendingAsset> ordered)
    {
        Span<byte> idBytes = stackalloc byte[OapFormat.AssetIdSize];
        foreach (var asset in ordered)
        {
            foreach (var dependency in asset.Dependencies)
            {
                OapFormat.WriteAssetId(dependency, idBytes);
                writer.Write(idBytes);
            }
        }
    }

    static void WriteIndex(PaddedWriter writer, OapIndexEntry[] index)
    {
        Span<byte> entryBytes = stackalloc byte[OapFormat.IndexEntrySize];
        foreach (var entry in index)
        {
            entry.Write(entryBytes);
            writer.Write(entryBytes);
        }
    }

    /// <summary>Writes the package to <paramref name="path"/>, replacing a previous file atomically by rename.</summary>
    /// <param name="path">The destination file path.</param>
    public void WriteToFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Replacing by rename leaves sessions that still read the previous package on its old bytes.
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            WriteTo(file);
        }

        File.Move(temporary, path, overwrite: true);
    }

    PackageLayout Layout()
    {
        var position = (long)OapFormat.HeaderSize;
        var dataOffsets = new Dictionary<PendingAsset, long>(ReferenceEqualityComparer.Instance);
        foreach (var asset in assets)
        {
            if (asset.Stored.Length == 0) continue;
            var alignment = Alignment.For(asset.Stored.Length);
            position = (position + alignment - 1) / alignment * alignment;
            dataOffsets[asset] = position;
            position += asset.Stored.Length;
        }

        var ordered = assets.OrderBy(static a => a.AssetId, OapAssetIdComparer.Instance).ToList();
        var index = new OapIndexEntry[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            var asset = ordered[i];
            index[i] = asset.Entry(dataOffsets.GetValueOrDefault(asset), position);
            position += asset.Dependencies.Length * OapFormat.AssetIdSize;
        }

        var stringTableOffset = position;
        for (var i = 0; i < ordered.Count; i++)
        {
            index[i] = index[i] with { VirtualPathOffset = checked((uint)position) };
            position += ordered[i].VirtualPath.Length;
        }

        var stringTableSize = position - stringTableOffset;
        var manifestSize = manifest?.Length ?? 0;
        var manifestOffset = manifestSize > 0 ? position : 0;
        var indexOffset = position + manifestSize;

        var header = new OapHeader
        {
            Flags = HeaderFlags(manifestSize > 0),
            EntryCount = (uint)ordered.Count,
            IndexOffset = (ulong)indexOffset,
            IndexSize = (ulong)ordered.Count * OapFormat.IndexEntrySize,
            StringTableOffset = (ulong)stringTableOffset,
            StringTableSize = (ulong)stringTableSize,
            ManifestOffset = (ulong)manifestOffset,
            ManifestSize = (uint)manifestSize
        };
        return new PackageLayout(header, ordered, index, dataOffsets);
    }

    OapFlags HeaderFlags(bool hasManifest)
    {
        var flags = OapFlags.SortedIndex;
        if (hasManifest) flags |= OapFlags.HasManifest;
        if (assets.Any(static a => a.Encryption != OapEncryption.None)) flags |= OapFlags.Encrypted;
        if (assets.Any(static a => (a.Flags & OapEntryFlags.Tombstone) != 0)) flags |= OapFlags.HasTombstones;
        return flags;
    }

    sealed record PackageLayout(
        OapHeader Header,
        List<PendingAsset> Ordered,
        OapIndexEntry[] Index,
        Dictionary<PendingAsset, long> DataOffsets);

    // Counts what it writes so padding is computed against the package start, not the stream's position.
    sealed class PaddedWriter(Stream output)
    {
        static readonly byte[] Zeros = new byte[4096];
        long written;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            output.Write(bytes);
            written += bytes.Length;
        }

        public void PadTo(long offset)
        {
            while (written < offset) Write(Zeros.AsSpan(0, (int)Math.Min(Zeros.Length, offset - written)));
        }
    }

    sealed class PendingAsset
    {
        public Guid AssetId { get; init; }
        public byte[] VirtualPath { get; init; } = [];
        public byte AssetType { get; init; }
        public OapCompression Compression { get; init; }
        public bool IsChunked { get; init; }
        public OapEncryption Encryption { get; init; }
        public OapEntryFlags Flags { get; init; }
        public ulong UncompressedSize { get; init; }
        public uint ContentCrc32 { get; init; }
        public byte[] Stored { get; init; } = [];
        public Guid[] Dependencies { get; init; } = [];

        public OapIndexEntry Entry(long dataOffset, long dependencyOffset) => new()
        {
            AssetId = AssetId,
            DataOffset = (ulong)dataOffset,
            StoredSize = (ulong)Stored.Length,
            UncompressedSize = UncompressedSize,
            ContentCrc32 = ContentCrc32,
            Compression = Compression,
            IsChunked = IsChunked,
            AssetType = AssetType,
            Encryption = Encryption,
            Flags = Flags,
            VirtualPathLength = (ushort)VirtualPath.Length,
            DependencyCount = (ushort)Dependencies.Length,
            DependencyOffset = Dependencies.Length == 0 ? 0 : (ulong)dependencyOffset
        };
    }
}

/// <summary>
/// Orders <see cref="System.Guid"/> values by their raw big-endian bytes, matching the
/// lexicographic sort the OAP index uses so binary search stays correct.
/// </summary>
public sealed class OapAssetIdComparer : IComparer<Guid>
{
    /// <summary>The shared comparer instance.</summary>
    public static OapAssetIdComparer Instance { get; } = new();

    /// <inheritdoc/>
    public int Compare(Guid x, Guid y)
    {
        Span<byte> a = stackalloc byte[OapFormat.AssetIdSize];
        Span<byte> b = stackalloc byte[OapFormat.AssetIdSize];
        OapFormat.WriteAssetId(x, a);
        OapFormat.WriteAssetId(y, b);
        return a.SequenceCompareTo(b);
    }
}
