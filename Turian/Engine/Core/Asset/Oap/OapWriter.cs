namespace Turian.Engine.Core;

/// <summary>
/// How a newly added asset should be compressed.
/// </summary>
public readonly record struct OapCompressChoice
{
    OapCompressChoice(bool auto, OapCompression fixedCodec)
    {
        IsAuto = auto;
        FixedCodec = fixedCodec;
    }

    /// <summary>Gets a value indicating whether the smaller of store/deflate is chosen automatically.</summary>
    public bool IsAuto { get; }

    /// <summary>Gets the forced codec when <see cref="IsAuto"/> is <see langword="false"/>.</summary>
    public OapCompression FixedCodec { get; }

    /// <summary>Pick the smaller of <see cref="OapCompression.Store"/> and <see cref="OapCompression.Deflate"/>.</summary>
    public static OapCompressChoice Auto { get; } = new(auto: true, OapCompression.Store);

    /// <summary>Force a specific codec.</summary>
    /// <param name="codec">The codec to use for every asset.</param>
    /// <returns>A fixed-codec choice.</returns>
    public static OapCompressChoice Fixed(OapCompression codec) => new(auto: false, codec);
}

/// <summary>
/// Builds an Open Asset Package. Assets are added one at a time (compressed and
/// encrypted eagerly), then <see cref="Serialize"/> lays the whole file out in memory
/// and <see cref="WriteToFile"/> flushes it. Entries are emitted sorted by
/// <c>asset_id</c> so readers can binary-search.
/// </summary>
public sealed class OapWriter
{
    readonly List<PendingAsset> assets = [];
    byte[]? manifest;
    byte[]? key;

    /// <summary>Gets the number of assets added so far.</summary>
    public int Count => assets.Count;

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
    /// <param name="virtualPath">A human-readable path such as <c>textures/hero.amtex</c>. May be empty.</param>
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

        var choice = compression == default ? OapCompressChoice.Auto : compression;
        var crc = OapFormat.Crc32(data);

        OapCompression codec;
        byte[] stored;
        if (choice.IsAuto)
        {
            (codec, stored) = OapCompressionCodec.Best(data);
        }
        else
        {
            codec = choice.FixedCodec;
            stored = OapCompressionCodec.Compress(codec, data);
        }

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
            Encryption = encryption,
            UncompressedSize = (ulong)data.Length,
            ContentCrc32 = crc,
            Stored = stored,
            Dependencies = dependencies is null ? [] : [.. dependencies]
        });
    }

    /// <summary>
    /// Lays the whole package out into a single freshly allocated array. Entries are
    /// emitted sorted by <c>asset_id</c>.
    /// </summary>
    /// <returns>The complete <c>.oap</c> bytes.</returns>
    public byte[] Serialize()
    {
        var ordered = assets
            .OrderBy(static a => a.AssetId, OapAssetIdComparer.Instance)
            .ToList();

        var index = new OapIndexEntry[ordered.Count];
        using var buffer = new MemoryStream();

        // Header placeholder — patched once final offsets are known.
        buffer.Write(new byte[OapFormat.HeaderSize]);

        // Asset data blobs.
        for (var i = 0; i < ordered.Count; i++)
        {
            var asset = ordered[i];
            index[i] = new OapIndexEntry
            {
                AssetId = asset.AssetId,
                DataOffset = (ulong)buffer.Position,
                StoredSize = (ulong)asset.Stored.Length,
                UncompressedSize = asset.UncompressedSize,
                ContentCrc32 = asset.ContentCrc32,
                Compression = asset.Compression,
                AssetType = asset.AssetType,
                Encryption = asset.Encryption
            };
            buffer.Write(asset.Stored);
        }

        // Dependency lists.
        Span<byte> depIdBytes = stackalloc byte[OapFormat.AssetIdSize];
        for (var i = 0; i < ordered.Count; i++)
        {
            var deps = ordered[i].Dependencies;
            if (deps.Length == 0)
            {
                continue;
            }

            index[i] = index[i] with
            {
                DependencyOffset = (ulong)buffer.Position,
                DependencyCount = (ushort)deps.Length
            };

            foreach (var dep in deps)
            {
                OapFormat.WriteAssetId(dep, depIdBytes);
                buffer.Write(depIdBytes);
            }
        }

        // String table (virtual paths).
        var stringTableOffset = (ulong)buffer.Position;
        for (var i = 0; i < ordered.Count; i++)
        {
            var path = ordered[i].VirtualPath;
            index[i] = index[i] with
            {
                VirtualPathOffset = (uint)buffer.Position,
                VirtualPathLength = (ushort)path.Length
            };
            buffer.Write(path);
        }

        var stringTableSize = (ulong)buffer.Position - stringTableOffset;

        // Manifest.
        ulong manifestOffset = 0;
        uint manifestSize = 0;
        if (manifest is { Length: > 0 })
        {
            manifestOffset = (ulong)buffer.Position;
            manifestSize = (uint)manifest.Length;
            buffer.Write(manifest);
        }

        // Index.
        var indexOffset = (ulong)buffer.Position;
        Span<byte> entryBytes = stackalloc byte[OapFormat.IndexEntrySize];
        foreach (var entry in index)
        {
            entry.Write(entryBytes);
            buffer.Write(entryBytes);
        }

        var flags = OapFlags.SortedIndex;
        if (manifestSize > 0)
        {
            flags |= OapFlags.HasManifest;
        }

        if (ordered.Any(static a => a.Encryption != OapEncryption.None))
        {
            flags |= OapFlags.Encrypted;
        }

        var header = new OapHeader
        {
            Flags = flags,
            EntryCount = (uint)ordered.Count,
            IndexOffset = indexOffset,
            IndexSize = (ulong)ordered.Count * OapFormat.IndexEntrySize,
            StringTableOffset = stringTableOffset,
            StringTableSize = stringTableSize,
            ManifestOffset = manifestOffset,
            ManifestSize = manifestSize
        };

        var bytes = buffer.ToArray();
        header.Write(bytes);
        return bytes;
    }

    /// <summary>Serialises the package and writes it to <paramref name="path"/>.</summary>
    /// <param name="path">The destination file path.</param>
    public void WriteToFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, Serialize());
    }

    sealed class PendingAsset
    {
        public Guid AssetId { get; init; }
        public byte[] VirtualPath { get; init; } = [];
        public byte AssetType { get; init; }
        public OapCompression Compression { get; init; }
        public OapEncryption Encryption { get; init; }
        public ulong UncompressedSize { get; init; }
        public uint ContentCrc32 { get; init; }
        public byte[] Stored { get; init; } = [];
        public Guid[] Dependencies { get; init; } = [];
    }
}

/// <summary>
/// Orders <see cref="Guid"/> values by their raw big-endian bytes, matching the
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
