namespace Turian.Editor.CLI;

/// <summary>
/// Writes BGRA8 pixels — the format the offscreen frame target reads back — as an 8-bit RGB PNG.
/// </summary>
static class PngWriter
{
    static readonly byte[] signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Writes <paramref name="bgra"/> to <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">Absolute path of the file to write.</param>
    /// <param name="bgra">Pixel data, <paramref name="width"/> × <paramref name="height"/> × 4 bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    public static void Save(string filePath, ReadOnlySpan<byte> bgra, uint width, uint height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var expected = (int)(width * height * 4);
        if (bgra.Length < expected)
        {
            throw new ArgumentException($"Expected at least {expected} bytes of pixel data.", nameof(bgra));
        }

        // One filter byte (0 = None) followed by RGB triplets, per scanline.
        var raw = new byte[height * (1 + (width * 3))];
        var target = 0;
        for (var y = 0; y < height; y++)
        {
            raw[target++] = 0;
            var row = y * (int)width * 4;
            for (var x = 0; x < width; x++)
            {
                var pixel = row + (x * 4);
                raw[target++] = bgra[pixel + 2];
                raw[target++] = bgra[pixel + 1];
                raw[target++] = bgra[pixel];
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        using var file = File.Create(filePath);
        file.Write(signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // color type: truecolor
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace

        WriteChunk(file, "IHDR", header);
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        crc = Accumulate(crc, first);
        crc = Accumulate(crc, second);
        return crc ^ 0xFFFFFFFFu;
    }

    static uint Accumulate(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc;
    }
}
