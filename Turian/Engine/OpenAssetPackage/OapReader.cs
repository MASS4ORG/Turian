namespace OpenAssetPackage;

/// <summary>
/// Random-access reader for an Open Asset Package. The header, index, string table
/// and manifest are parsed once at open time; asset blobs are located purely through
/// the index and read (and decrypted / decompressed) on demand. Only the sections being
/// read are loaded, so a multi-gigabyte package can be mounted cheaply.
/// </summary>
public sealed class OapReader : IDisposable
{
    readonly IOapSource source;
    readonly bool ownsSource;
    readonly byte[] stringTable;
    readonly byte[]? manifestBytes;
    byte[]? key;

    OapReader(
        IOapSource source,
        bool ownsSource,
        OapHeader header,
        OapIndexEntry[] entries,
        byte[] stringTable,
        byte[]? manifestBytes,
        string packageHash)
    {
        this.source = source;
        this.ownsSource = ownsSource;
        Header = header;
        Entries = entries;
        this.stringTable = stringTable;
        this.manifestBytes = manifestBytes;
        PackageHash = packageHash;
    }

    /// <summary>Gets the parsed file header.</summary>
    public OapHeader Header { get; }

    /// <summary>Gets the parsed index entries, tombstones included, in stored (sorted) order.</summary>
    public IReadOnlyList<OapIndexEntry> Entries { get; }

    /// <summary>Gets the number of index entries.</summary>
    public int Count => Entries.Count;

    /// <summary>Gets a value indicating whether the index is sorted by <c>asset_id</c>.</summary>
    public bool IsSortedIndex => (Header.Flags & OapFlags.SortedIndex) != 0;

    /// <summary>
    /// Gets a hex hash of the metadata sections (header, index, string table, manifest). It changes whenever any
    /// asset, path, codec or manifest field changes, so it identifies a package build where file timestamps mean
    /// nothing (read-only media, network sources).
    /// </summary>
    public string PackageHash { get; }

    /// <summary>Opens a package held entirely in memory. The array is referenced, not copied.</summary>
    /// <param name="bytes">The complete <c>.oap</c> bytes.</param>
    /// <returns>An open reader.</returns>
    public static OapReader Open(byte[] bytes) => Open(new OapMemorySource(bytes));

    /// <summary>Opens a package over a source, reading only its metadata sections.</summary>
    /// <param name="source">The package bytes; they must stay readable while the reader is used.</param>
    /// <param name="ownsSource">Whether disposing the reader disposes <paramref name="source"/>.</param>
    /// <returns>An open reader.</returns>
    public static OapReader Open(IOapSource source, bool ownsSource = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (header, entries, stringTable, manifest, hash) = Parse(source);
        return new OapReader(source, ownsSource, header, entries, stringTable, manifest, hash);
    }

    /// <summary>Opens a package file for tools and tests; the reader owns the file handle.</summary>
    /// <param name="path">The path to the <c>.oap</c> file.</param>
    /// <returns>An open reader.</returns>
    /// <exception cref="FileNotFoundException">The package does not exist.</exception>
    public static OapReader OpenFile(string path)
    {
        var source = OapFileSource.Open(path);
        try
        {
            return Open(source, ownsSource: true);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Provides the key used to decrypt encrypted assets. Derive one from a passphrase
    /// with <see cref="OapCrypto.DeriveKey"/>.
    /// </summary>
    /// <param name="decryptionKey">A 32-byte key.</param>
    public void SetKey(ReadOnlySpan<byte> decryptionKey)
    {
        if (decryptionKey.Length != OapCrypto.KeyLength)
        {
            throw new ArgumentException($"An OAP key must be {OapCrypto.KeyLength} bytes.", nameof(decryptionKey));
        }

        key = [.. decryptionKey];
    }

    /// <summary>Gets the entry at <paramref name="index"/> in stored order.</summary>
    /// <param name="index">The zero-based position.</param>
    /// <returns>The index entry.</returns>
    public OapIndexEntry EntryAt(int index) => Entries[index];

    /// <summary>
    /// Finds an entry by its 128-bit id; binary search when the index is sorted. The result may be a tombstone,
    /// which a layered lookup treats as "removed" (<see cref="OapIndexEntry.IsTombstone"/>).
    /// </summary>
    /// <param name="id">The asset id.</param>
    /// <returns>The matching entry, or <see langword="null"/> when absent.</returns>
    public OapIndexEntry? FindById(Guid id)
    {
        if (IsSortedIndex)
        {
            int lo = 0, hi = Entries.Count - 1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                var cmp = OapAssetIdComparer.Instance.Compare(Entries[mid].AssetId, id);
                if (cmp == 0)
                {
                    return Entries[mid];
                }

                if (cmp < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return null;
        }

        foreach (var entry in Entries)
        {
            if (entry.AssetId == id)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Finds an entry by virtual path (linear scan; paths are a secondary key). May be a tombstone.</summary>
    /// <param name="virtualPath">The path to match, compared as UTF-8.</param>
    /// <returns>The matching entry, or <see langword="null"/> when absent.</returns>
    public OapIndexEntry? FindByPath(string virtualPath)
    {
        ArgumentNullException.ThrowIfNull(virtualPath);
        foreach (var entry in Entries)
        {
            if (string.Equals(VirtualPath(entry), virtualPath, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Gets the virtual path declared for <paramref name="entry"/>.</summary>
    /// <param name="entry">The index entry.</param>
    /// <returns>The path, or an empty string when the entry declares none.</returns>
    public string VirtualPath(OapIndexEntry entry)
    {
        if (entry.VirtualPathLength == 0)
        {
            return string.Empty;
        }

        var start = entry.VirtualPathOffset - (long)Header.StringTableOffset;
        if (start < 0 || start + entry.VirtualPathLength > stringTable.Length)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(stringTable, (int)start, entry.VirtualPathLength);
    }

    /// <summary>Gets the dependency ids declared for <paramref name="entry"/>.</summary>
    /// <param name="entry">The index entry.</param>
    /// <returns>The dependency ids, in declared order.</returns>
    public Guid[] Dependencies(OapIndexEntry entry)
    {
        if (entry.DependencyCount == 0)
        {
            return [];
        }

        var bytes = ReadRange((long)entry.DependencyOffset, entry.DependencyCount * OapFormat.AssetIdSize);
        var ids = new Guid[entry.DependencyCount];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = OapFormat.ReadAssetId(bytes.AsSpan(i * OapFormat.AssetIdSize));
        }

        return ids;
    }

    /// <summary>Gets the package manifest bytes, or <see langword="null"/> when there is no manifest.</summary>
    public ReadOnlyMemory<byte>? Manifest => manifestBytes;

    /// <summary>
    /// Reads an asset's plaintext bytes, decrypting and decompressing as needed. When
    /// <paramref name="verify"/> is set the content CRC-32 (and each chunked block's CRC-32) is checked — a wrong
    /// decryption key surfaces here as an <see cref="OapCorruptDataException"/>.
    /// </summary>
    /// <param name="entry">The index entry to read.</param>
    /// <param name="verify">Whether to verify the content CRC-32.</param>
    /// <returns>The plaintext asset bytes.</returns>
    /// <exception cref="OapNotFoundException">The entry is a tombstone.</exception>
    public byte[] ReadAsset(OapIndexEntry entry, bool verify = true)
    {
        ThrowIfTombstone(entry);
        var stored = ReadRange((long)entry.DataOffset, checked((int)entry.StoredSize));

        if (entry.Encryption != OapEncryption.None)
        {
            if (key is null)
            {
                throw new OapKeyRequiredException();
            }

            Span<byte> idBytes = stackalloc byte[OapFormat.AssetIdSize];
            OapFormat.WriteAssetId(entry.AssetId, idBytes);
            OapCrypto.Apply(entry.Encryption, key, idBytes, entry.ContentCrc32, stored);
        }

        var plaintext = Decode(entry, stored, verify);
        if (verify && OapFormat.Crc32(plaintext) != entry.ContentCrc32)
        {
            throw new OapCorruptDataException(
                $"OAP asset {entry.AssetId:D} failed its content CRC-32 check (wrong key or corrupt data).");
        }

        return plaintext;
    }

    /// <summary>
    /// Reads part of an asset's plaintext. Stored and chunked assets read only the bytes or blocks the range covers;
    /// a whole-blob compressed asset is decoded in full first.
    /// </summary>
    /// <param name="entry">The index entry to read.</param>
    /// <param name="offset">The plaintext offset to start at.</param>
    /// <param name="buffer">The destination.</param>
    /// <returns>The number of bytes read; fewer than requested only at the end of the asset.</returns>
    /// <exception cref="OapNotFoundException">The entry is a tombstone.</exception>
    public int ReadAssetRange(OapIndexEntry entry, long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ThrowIfTombstone(entry);
        if (IsRandomAccess(entry))
            return new OapAssetRangeReader(source, entry, key, verify: true).ReadAt(offset, buffer);

        var plaintext = ReadAsset(entry, verify: false);
        if (offset >= plaintext.Length) return 0;
        var count = (int)Math.Min(buffer.Length, plaintext.Length - offset);
        plaintext.AsSpan((int)offset, count).CopyTo(buffer);
        return count;
    }

    /// <summary>
    /// Opens a seekable, read-only stream over an asset's plaintext. Stored and chunked assets are read from the
    /// package on demand, decrypting by position and decoding one block at a time; a whole-blob compressed asset is
    /// decoded into memory first. The stream reads the reader's source, so it must be disposed before the reader.
    /// </summary>
    /// <param name="entry">The index entry to read.</param>
    /// <returns>A readable stream the caller disposes.</returns>
    /// <exception cref="OapNotFoundException">The entry is a tombstone.</exception>
    public Stream OpenAssetStream(OapIndexEntry entry)
    {
        ThrowIfTombstone(entry);
        return IsRandomAccess(entry)
            ? new OapAssetStream(new OapAssetRangeReader(source, entry, key, verify: true))
            : new MemoryStream(ReadAsset(entry, verify: false), writable: false);
    }

    /// <summary>Releases the backing source when the reader owns it and it is disposable.</summary>
    public void Dispose()
    {
        if (ownsSource && source is IDisposable disposable) disposable.Dispose();
    }

    static bool IsRandomAccess(OapIndexEntry entry) => entry.IsChunked || entry.Compression == OapCompression.Store;

    static void ThrowIfTombstone(OapIndexEntry entry)
    {
        if (entry.IsTombstone) throw new OapNotFoundException(entry.AssetId.ToString("D"));
    }

    static byte[] Decode(OapIndexEntry entry, byte[] stored, bool verify)
    {
        if (entry.IsChunked)
        {
            if (entry.Compression == OapCompression.Store)
                throw new OapCorruptDataException($"OAP asset {entry.AssetId:D} is chunked without a codec.");
            return OapChunks.Decode(stored, entry.Compression, entry.UncompressedSize, verify);
        }

        if (entry.Compression != OapCompression.Store)
            return OapCompressionCodec.Decompress(entry.Compression, stored, (long)entry.UncompressedSize);
        if ((ulong)stored.Length != entry.UncompressedSize)
            throw new OapCorruptDataException("Stored OAP blob length does not match its index entry.");
        return stored;
    }

    byte[] ReadRange(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > source.Length)
        {
            throw new OapTruncatedException("OAP range read runs past the end of the package.");
        }

        return ReadSection(source, offset, length);
    }

    static byte[] ReadSection(IOapSource source, long offset, int count)
    {
        var buffer = new byte[count];
        if (source.ReadAt(offset, buffer) != count)
        {
            throw new OapTruncatedException("OAP range read runs past the end of the package.");
        }

        return buffer;
    }

    static (OapHeader Header, OapIndexEntry[] Entries, byte[] StringTable, byte[]? Manifest, string Hash) Parse(
        IOapSource source)
    {
        if (source.Length < OapFormat.HeaderSize)
        {
            throw new OapTruncatedException("OAP data is shorter than its 64-byte header.");
        }

        var headerBytes = ReadSection(source, 0, OapFormat.HeaderSize);
        var header = OapHeader.Read(headerBytes);
        var (indexBytes, entries) = ReadIndex(source, header);
        var stringTable = ReadStringTable(source, header);
        var manifest = ReadManifest(source, header);

        var hasher = new System.IO.Hashing.XxHash128();
        hasher.Append(headerBytes);
        hasher.Append(indexBytes);
        hasher.Append(stringTable);
        if (manifest is not null) hasher.Append(manifest);
        return (header, entries, stringTable, manifest, Convert.ToHexStringLower(hasher.GetCurrentHash()));
    }

    static (byte[] Bytes, OapIndexEntry[] Entries) ReadIndex(IOapSource source, OapHeader header)
    {
        var length = source.Length;
        var indexOffset = (long)header.IndexOffset;
        var indexSize = (long)header.IndexSize;
        if (indexOffset < OapFormat.HeaderSize || indexOffset + indexSize > length)
        {
            throw new OapTruncatedException("OAP index lies outside the package.");
        }

        if (header.IndexSize != (ulong)header.EntryCount * OapFormat.IndexEntrySize)
        {
            throw new OapCorruptDataException("OAP index size does not match its entry count.");
        }

        var indexBytes = header.EntryCount > 0 ? ReadSection(source, indexOffset, checked((int)indexSize)) : [];
        var entries = new OapIndexEntry[header.EntryCount];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = OapIndexEntry.Read(indexBytes.AsSpan(i * OapFormat.IndexEntrySize));
            if ((long)entry.DataOffset + (long)entry.StoredSize > length)
            {
                throw new OapTruncatedException($"OAP asset {entry.AssetId:D} blob runs past the end of the package.");
            }

            entries[i] = entry;
        }

        return (indexBytes, entries);
    }

    static byte[] ReadStringTable(IOapSource source, OapHeader header)
    {
        var offset = (long)header.StringTableOffset;
        var size = checked((int)header.StringTableSize);
        if (size == 0) return [];
        return offset >= 0 && offset + size <= source.Length
            ? ReadSection(source, offset, size)
            : throw new OapTruncatedException("OAP string table lies outside the package.");
    }

    static byte[]? ReadManifest(IOapSource source, OapHeader header)
    {
        if (header.ManifestOffset == 0 || header.ManifestSize == 0) return null;

        var offset = (long)header.ManifestOffset;
        if (offset < OapFormat.HeaderSize || offset + header.ManifestSize > source.Length)
        {
            throw new OapTruncatedException("OAP manifest lies outside the package.");
        }

        return ReadSection(source, offset, checked((int)header.ManifestSize));
    }
}
