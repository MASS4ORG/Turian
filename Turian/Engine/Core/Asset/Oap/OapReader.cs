namespace Turian.Engine.Core;

/// <summary>
/// Random-access reader for an Open Asset Package. The header, index, string table
/// and manifest are parsed once at open time; asset blobs are located purely through
/// the index and read (and decrypted / decompressed) on demand. A package opened from
/// a file never loads more than the section being read, so a multi-gigabyte package
/// can be mounted cheaply.
/// </summary>
public sealed class OapReader
{
    readonly byte[]? memory;
    readonly string? filePath;
    readonly long backingLength;
    readonly byte[] stringTable;
    readonly byte[]? manifestBytes;
    byte[]? key;

    OapReader(
        byte[]? memory,
        string? filePath,
        long backingLength,
        OapHeader header,
        OapIndexEntry[] entries,
        byte[] stringTable,
        byte[]? manifestBytes)
    {
        this.memory = memory;
        this.filePath = filePath;
        this.backingLength = backingLength;
        Header = header;
        Entries = entries;
        this.stringTable = stringTable;
        this.manifestBytes = manifestBytes;
    }

    /// <summary>Gets the parsed file header.</summary>
    public OapHeader Header { get; }

    /// <summary>Gets the parsed index entries, in stored (sorted) order.</summary>
    public IReadOnlyList<OapIndexEntry> Entries { get; }

    /// <summary>Gets the number of assets in the package.</summary>
    public int Count => Entries.Count;

    /// <summary>Gets a value indicating whether the index is sorted by <c>asset_id</c>.</summary>
    public bool IsSortedIndex => (Header.Flags & OapFlags.SortedIndex) != 0;

    /// <summary>Opens a package held entirely in memory. The array is referenced, not copied.</summary>
    /// <param name="bytes">The complete <c>.oap</c> bytes.</param>
    /// <returns>An open reader.</returns>
    public static OapReader Open(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var (header, entries, stringTable, manifest) = Parse(
            bytes.LongLength,
            (offset, count) => [.. bytes.AsSpan((int)offset, count)]);
        return new OapReader(bytes, filePath: null, bytes.LongLength, header, entries, stringTable, manifest);
    }

    /// <summary>Opens a package backed by a file, reading only its metadata sections.</summary>
    /// <param name="path">The absolute path to the <c>.oap</c> file.</param>
    /// <returns>An open reader.</returns>
    public static OapReader OpenFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("OAP package was not found.", path);
        }

        var length = new FileInfo(path).Length;

        var (header, entries, stringTable, manifest) = Parse(
            length,
            (offset, count) => ReadFileSection(path, offset, count));

        return new OapReader(memory: null, path, length, header, entries, stringTable, manifest);
    }

    static byte[] ReadFileSection(string path, long offset, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
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

    /// <summary>Finds an asset by its 128-bit id. Binary search when the index is sorted.</summary>
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

    /// <summary>Finds an asset by its virtual path (linear scan; paths are a secondary key).</summary>
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
    /// <paramref name="verify"/> is set the content CRC-32 is checked — a wrong
    /// decryption key surfaces here as an <see cref="OapCorruptDataException"/>.
    /// </summary>
    /// <param name="entry">The index entry to read.</param>
    /// <param name="verify">Whether to verify the content CRC-32.</param>
    /// <returns>The plaintext asset bytes.</returns>
    public byte[] ReadAsset(OapIndexEntry entry, bool verify = true)
    {
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

        byte[] plaintext;
        if (entry.Compression == OapCompression.Store)
        {
            if ((ulong)stored.Length != entry.UncompressedSize)
            {
                throw new OapCorruptDataException("Stored OAP blob length does not match its index entry.");
            }

            plaintext = stored;
        }
        else
        {
            plaintext = OapCompressionCodec.Decompress(entry.Compression, stored, (long)entry.UncompressedSize);
        }

        if (verify && OapFormat.Crc32(plaintext) != entry.ContentCrc32)
        {
            throw new OapCorruptDataException(
                $"OAP asset {entry.AssetId:D} failed its content CRC-32 check (wrong key or corrupt data).");
        }

        return plaintext;
    }

    /// <summary>
    /// Opens a readable stream over an asset's plaintext bytes. For a stored,
    /// unencrypted asset this is a bounded window over the package with no copy and no
    /// full-file load; otherwise the asset is decoded into memory first.
    /// </summary>
    /// <param name="entry">The index entry to read.</param>
    /// <returns>A readable stream the caller disposes.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the FileStream transfers to the returned SubStream, which disposes it.")]
    public Stream OpenAssetStream(OapIndexEntry entry)
    {
        if (entry.Compression == OapCompression.Store && entry.Encryption == OapEncryption.None)
        {
            if (memory is not null)
            {
                return new MemoryStream(memory, (int)entry.DataOffset, (int)entry.StoredSize, writable: false);
            }

            FileStream? file = null;
            try
            {
                file = new FileStream(filePath!, FileMode.Open, FileAccess.Read, FileShare.Read);
                file.Position = (long)entry.DataOffset;
                var sub = new SubStream(file, (long)entry.StoredSize);
                file = null; // ownership transferred
                return sub;
            }
            finally
            {
                file?.Dispose();
            }
        }

        return new MemoryStream(ReadAsset(entry, verify: false), writable: false);
    }

    byte[] ReadRange(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > backingLength)
        {
            throw new OapTruncatedException("OAP range read runs past the end of the package.");
        }

        if (memory is not null)
        {
            return [.. memory.AsSpan((int)offset, length)];
        }

        using var stream = new FileStream(filePath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    static (OapHeader Header, OapIndexEntry[] Entries, byte[] StringTable, byte[]? Manifest) Parse(
        long length,
        Func<long, int, byte[]> read)
    {
        if (length < OapFormat.HeaderSize)
        {
            throw new OapTruncatedException("OAP data is shorter than its 64-byte header.");
        }

        var header = OapHeader.Read(read(0, OapFormat.HeaderSize));

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

        var entries = new OapIndexEntry[header.EntryCount];
        if (header.EntryCount > 0)
        {
            var indexBytes = read(indexOffset, checked((int)indexSize));
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = OapIndexEntry.Read(indexBytes.AsSpan(i * OapFormat.IndexEntrySize));
                if ((long)entry.DataOffset + (long)entry.StoredSize > length)
                {
                    throw new OapTruncatedException($"OAP asset {entry.AssetId:D} blob runs past the end of the package.");
                }

                entries[i] = entry;
            }
        }

        var stringTableOffset = (long)header.StringTableOffset;
        var stringTableSize = checked((int)header.StringTableSize);
        var stringTable = stringTableSize == 0
            ? []
            : stringTableOffset >= 0 && stringTableOffset + stringTableSize <= length
                ? read(stringTableOffset, stringTableSize)
                : throw new OapTruncatedException("OAP string table lies outside the package.");

        byte[]? manifest = null;
        if (header.ManifestOffset != 0 && header.ManifestSize != 0)
        {
            var manifestOffset = (long)header.ManifestOffset;
            if (manifestOffset < OapFormat.HeaderSize || manifestOffset + header.ManifestSize > length)
            {
                throw new OapTruncatedException("OAP manifest lies outside the package.");
            }

            manifest = read(manifestOffset, checked((int)header.ManifestSize));
        }

        return (header, entries, stringTable, manifest);
    }
}
