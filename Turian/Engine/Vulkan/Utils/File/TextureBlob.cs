namespace Turian.Engine.Core;

/// <summary>
/// Reads the <c>.amtex</c> binary texture container: a 12-byte header followed by a binary manifest
/// chunk and a payload chunk holding the mip levels back to back. The payload is whatever the GPU
/// samples directly — block-compressed blocks or RGBA8 texels — so loading is a header read and a
/// set of slices.
/// </summary>
public sealed class TextureBlob
{
    /// <summary>File extension of the container.</summary>
    public const string FileExtension = ".amtex";

    /// <summary>Header magic, the ASCII bytes <c>AMTX</c>.</summary>
    public const uint Magic = 0x58544D41;

    /// <summary>Container version this reader accepts.</summary>
    public const uint Version = 1;

    /// <summary>Chunk type of the binary manifest, the ASCII bytes <c>MANI</c>.</summary>
    public const uint ManifestChunkType = 0x494E414D;

    /// <summary>Chunk type of the payload, the ASCII bytes <c>BIN\0</c>.</summary>
    public const uint PayloadChunkType = 0x004E4942;

    const int headerLength = 12;
    const int chunkHeaderLength = 8;

    /// <summary>Vulkan format of the stored levels.</summary>
    public Format Format { get; private init; }

    /// <summary>Width of mip level 0.</summary>
    public uint Width { get; private init; }

    /// <summary>Height of mip level 0.</summary>
    public uint Height { get; private init; }

    /// <summary>Whether the data is sampled in sRGB space, as recorded at import.</summary>
    public bool IsSrgb { get; private init; }

    /// <summary>The mip levels, largest first. Slices over the container bytes.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Levels { get; private init; } = [];

    /// <summary>Reads a texture blob from a file.</summary>
    /// <param name="path">Absolute path of the <c>.amtex</c> file.</param>
    public static TextureBlob Load(string path) => Read(File.ReadAllBytes(path));

    /// <summary>Reads a texture blob from a stream, consuming it to the end.</summary>
    /// <param name="stream">The stream positioned at the container header.</param>
    public static TextureBlob Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    /// <summary>Reads a texture blob from an in-memory container.</summary>
    /// <param name="data">The complete container bytes.</param>
    /// <exception cref="InvalidDataException">The container is malformed or a newer version.</exception>
    public static TextureBlob Read(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (span.Length < headerLength)
        {
            throw new InvalidDataException("Texture blob is shorter than its header.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
        {
            throw new InvalidDataException("Texture blob magic does not match.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported texture blob version {version}.");
        }

        var totalLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        if (totalLength > span.Length)
        {
            throw new InvalidDataException("Texture blob is truncated.");
        }

        ReadOnlyMemory<byte> manifest = default;
        ReadOnlyMemory<byte> payload = default;

        var cursor = headerLength;
        while (cursor + chunkHeaderLength <= totalLength)
        {
            var chunkLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[cursor..]);
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 4)..]);
            cursor += chunkHeaderLength;

            if (cursor + chunkLength > totalLength)
            {
                throw new InvalidDataException("Texture blob chunk runs past the end of the file.");
            }

            var chunk = data.Slice(cursor, chunkLength);
            if (chunkType == ManifestChunkType) manifest = chunk;
            else if (chunkType == PayloadChunkType) payload = chunk;

            cursor += chunkLength;
        }

        if (manifest.IsEmpty)
        {
            throw new InvalidDataException("Texture blob has no manifest chunk.");
        }

        return ReadManifest(manifest.Span, payload);
    }

    /// <summary>
    /// Sniffs whether <paramref name="data"/> opens with the texture blob magic, so a loader can
    /// choose this path over decoding a source image format.
    /// </summary>
    /// <param name="data">Bytes to sniff; may be shorter than a full header.</param>
    public static bool IsTextureBlob(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    static TextureBlob ReadManifest(ReadOnlySpan<byte> manifest, ReadOnlyMemory<byte> payload)
    {
        var cursor = 0;

        var format = (Format)ReadUInt32(manifest, ref cursor);
        var width = ReadUInt32(manifest, ref cursor);
        var height = ReadUInt32(manifest, ref cursor);
        var isSrgb = ReadUInt32(manifest, ref cursor) != 0;
        var levelCount = ReadUInt32(manifest, ref cursor);

        var levels = new ReadOnlyMemory<byte>[levelCount];
        for (var level = 0; level < levelCount; level++)
        {
            var byteOffset = (int)ReadUInt32(manifest, ref cursor);
            var byteLength = (int)ReadUInt32(manifest, ref cursor);

            if (byteOffset + byteLength > payload.Length)
            {
                throw new InvalidDataException($"Texture blob mip level {level} runs past the payload.");
            }

            levels[level] = payload.Slice(byteOffset, byteLength);
        }

        return new TextureBlob
        {
            Format = format,
            Width = width,
            Height = height,
            IsSrgb = isSrgb,
            Levels = levels,
        };
    }

    static uint ReadUInt32(ReadOnlySpan<byte> data, ref int cursor)
    {
        if (cursor + 4 > data.Length)
        {
            throw new InvalidDataException("Texture blob manifest ended unexpectedly.");
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
        cursor += 4;
        return value;
    }
}
