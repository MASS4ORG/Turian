namespace Turian.Engine.Core;

/// <summary>
/// Reads KTX2 containers: header, level index, data format descriptor and key/value data. Only payloads the GPU
/// samples directly are accepted, so levels are slices over the container bytes and nothing is decompressed.
/// </summary>
public static class Ktx2Reader
{
    /// <summary>File extension of the container.</summary>
    public const string FileExtension = ".ktx2";

    /// <summary>Length of the fixed header that precedes the level index.</summary>
    public const int HeaderLength = 80;

    /// <summary>Length of one level index entry.</summary>
    public const int LevelEntryLength = 24;

    /// <summary>The 12-byte file identifier, <c>«KTX 20»\r\n\x1A\n</c>.</summary>
    public static ReadOnlySpan<byte> Identifier =>
        [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Sniffs whether <paramref name="data"/> opens with the KTX2 identifier, so a loader can choose this path over
    /// decoding a source image format.
    /// </summary>
    /// <param name="data">Bytes to sniff; may be shorter than the identifier.</param>
    public static bool IsKtx2(ReadOnlySpan<byte> data) => data.StartsWith(Identifier);

    /// <summary>Reads a container from a file.</summary>
    /// <param name="path">Path of the <c>.ktx2</c> file.</param>
    public static Ktx2Texture Load(string path) => Read(File.ReadAllBytes(path));

    /// <summary>Reads a container from a stream, consuming it to the end.</summary>
    /// <param name="stream">The stream positioned at the container identifier.</param>
    public static Ktx2Texture Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    /// <summary>Reads a container that fills a whole content source.</summary>
    /// <param name="source">The source holding the container from offset zero.</param>
    /// <exception cref="InvalidDataException">The source ends before its declared length.</exception>
    public static Ktx2Texture Read(IContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var bytes = new byte[checked((int)source.Length)];
        var filled = 0;
        while (filled < bytes.Length)
        {
            var read = source.ReadAt(filled, bytes.AsSpan(filled));
            if (read == 0) throw new InvalidDataException("KTX2 content ended before its declared length.");
            filled += read;
        }

        return Read(bytes);
    }

    /// <summary>Reads an in-memory container. The returned levels and metadata are slices over it.</summary>
    /// <param name="data">The complete container bytes.</param>
    /// <exception cref="InvalidDataException">The container is malformed or its payload is not supported.</exception>
    public static Ktx2Texture Read(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (!IsKtx2(span)) throw new InvalidDataException("Not a KTX2 file: the identifier does not match.");
        if (span.Length < HeaderLength) throw new InvalidDataException("KTX2 file is shorter than its header.");

        var format = (Ktx2VkFormat)U32(span, 12);
        ValidateShape(format, depth: U32(span, 28), layers: U32(span, 32), faces: U32(span, 36), scheme: U32(span, 44));

        return new Ktx2Texture
        {
            Format = format,
            Width = U32(span, 20),
            Height = Math.Max(1u, U32(span, 24)),
            Levels = ReadLevels(data, Math.Max(1u, U32(span, 40))),
            DataFormatDescriptor = Slice(data, U32(span, 48), U32(span, 52)),
            KeyValues = ReadKeyValues(Slice(data, U32(span, 56), U32(span, 60))),
        };
    }

    static void ValidateShape(Ktx2VkFormat format, uint depth, uint layers, uint faces, uint scheme)
    {
        if (format == Ktx2VkFormat.Undefined)
            throw new InvalidDataException("KTX2 file has no VkFormat; Basis Universal payloads are not supported.");
        if (scheme != 0)
            throw new InvalidDataException($"KTX2 supercompression scheme {scheme} is not supported.");
        if (depth > 1 || layers > 1 || faces != 1)
            throw new InvalidDataException("Only 2D KTX2 textures with one layer and one face are supported.");
    }

    static ReadOnlyMemory<byte>[] ReadLevels(ReadOnlyMemory<byte> data, uint levelCount)
    {
        if ((ulong)HeaderLength + ((ulong)levelCount * LevelEntryLength) > (ulong)data.Length)
            throw new InvalidDataException("KTX2 level index runs past the end of the file.");

        var levels = new ReadOnlyMemory<byte>[levelCount];
        for (var level = 0; level < levels.Length; level++)
        {
            var entry = data.Span[(HeaderLength + (level * LevelEntryLength))..];
            levels[level] = Slice(data, BinaryPrimitives.ReadUInt64LittleEndian(entry),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]));
        }

        return levels;
    }

    static Dictionary<string, ReadOnlyMemory<byte>> ReadKeyValues(ReadOnlyMemory<byte> data)
    {
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var cursor = 0;
        while (cursor + 4 <= data.Length)
        {
            var length = (int)Math.Min(U32(data.Span, cursor), int.MaxValue);
            cursor += 4;
            if (length > data.Length - cursor)
                throw new InvalidDataException("KTX2 key/value entry runs past its section.");

            var entry = data.Slice(cursor, length);
            var keyEnd = entry.Span.IndexOf((byte)0);
            if (keyEnd <= 0) throw new InvalidDataException("KTX2 key/value entry has no NUL-terminated key.");

            entries[Encoding.UTF8.GetString(entry.Span[..keyEnd])] = entry[(keyEnd + 1)..];
            cursor += (length + 3) & ~3;
        }

        return entries;
    }

    static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> data, ulong offset, ulong length)
    {
        if (length == 0) return ReadOnlyMemory<byte>.Empty;
        if (offset > (ulong)data.Length || length > (ulong)data.Length - offset)
            throw new InvalidDataException("KTX2 section runs past the end of the file.");

        return data.Slice((int)offset, (int)length);
    }

    static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
