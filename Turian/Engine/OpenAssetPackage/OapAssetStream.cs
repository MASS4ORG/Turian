namespace OpenAssetPackage;

/// <summary>
/// Reads plaintext ranges of one stored or chunked asset straight from the package: stored bytes are decrypted in
/// place by position, and a chunked asset decodes only the blocks a read touches, keeping the last one cached.
/// Not thread-safe; each stream owns one.
/// </summary>
sealed class OapAssetRangeReader
{
    readonly IOapSource source;
    readonly OapIndexEntry entry;
    readonly byte[]? key;
    readonly OapBlockTable? table;
    readonly bool verify;
    byte[]? cachedBlock;
    int cachedIndex = -1;

    public OapAssetRangeReader(IOapSource source, OapIndexEntry entry, byte[]? key, bool verify)
    {
        if (entry.Encryption != OapEncryption.None && key is null) throw new OapKeyRequiredException();
        this.source = source;
        this.entry = entry;
        this.key = key;
        this.verify = verify;
        if (entry.IsChunked) table = ReadTable();
    }

    public long Length => (long)entry.UncompressedSize;

    /// <summary>Reads plaintext at <paramref name="offset"/>; returns fewer bytes only at the asset's end.</summary>
    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset >= Length || buffer.IsEmpty) return 0;
        var count = (int)Math.Min(buffer.Length, Length - offset);
        if (table is null)
        {
            ReadStored(offset, buffer[..count]);
            return count;
        }

        var done = 0;
        while (done < count)
        {
            var at = offset + done;
            var index = (int)(at / table.BlockSize);
            var within = (int)(at % table.BlockSize);
            var block = Block(index);
            var take = Math.Min(count - done, table.PlainLength(index) - within);
            block.AsSpan(within, take).CopyTo(buffer[done..]);
            done += take;
        }

        return count;
    }

    byte[] Block(int index)
    {
        if (index == cachedIndex) return cachedBlock!;
        var stored = new byte[table!.StoredLength(index)];
        ReadStored(table.StoredOffset(index), stored);
        cachedBlock ??= new byte[table.BlockSize];
        OapChunks.DecodeBlock(table, index, stored, entry.Compression, cachedBlock, verify);
        cachedIndex = index;
        return cachedBlock;
    }

    OapBlockTable ReadTable()
    {
        Span<byte> prefix = stackalloc byte[OapBlockTable.PrefixSize];
        ReadStored(0, prefix);
        var (blockSize, tableLength) = OapBlockTable.ReadPrefix(prefix, entry.UncompressedSize);
        var bytes = new byte[tableLength];
        ReadStored(OapBlockTable.PrefixSize, bytes);
        return OapBlockTable.Parse(blockSize, bytes, entry.UncompressedSize, entry.StoredSize);
    }

    // Reads stored blob bytes and undoes encryption by position, so no read ever needs the bytes before it.
    void ReadStored(long blobOffset, Span<byte> destination)
    {
        if (blobOffset + destination.Length > (long)entry.StoredSize)
            throw new OapTruncatedException("OAP read runs past the end of the asset blob.");
        if (source.ReadAt((long)entry.DataOffset + blobOffset, destination) != destination.Length)
            throw new OapTruncatedException("OAP range read runs past the end of the package.");
        if (entry.Encryption == OapEncryption.None) return;

        Span<byte> id = stackalloc byte[OapFormat.AssetIdSize];
        OapFormat.WriteAssetId(entry.AssetId, id);
        OapCrypto.Apply(entry.Encryption, key!, id, entry.ContentCrc32, destination, blobOffset);
    }
}

/// <summary>A seekable, read-only stream over an asset's plaintext that reads from the package on demand.</summary>
sealed class OapAssetStream(OapAssetRangeReader reader) : Stream
{
    long position;
    bool disposed;

    /// <inheritdoc />
    public override bool CanRead => !disposed;

    /// <inheritdoc />
    public override bool CanSeek => !disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => reader.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var read = reader.ReadAt(position, buffer);
        position += read;
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("Attempted to seek before the start of the asset.");

        position = target;
        return position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        disposed = true;
        base.Dispose(disposing);
    }
}
