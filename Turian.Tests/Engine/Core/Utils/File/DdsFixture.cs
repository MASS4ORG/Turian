namespace Turian.Tests;

/// <summary>
/// Builds DDS files in memory. A valid header is short enough to write by hand, which keeps the
/// fixtures readable and out of the repository as binaries.
/// </summary>
static class DdsFixture
{
    const int headerLength = 128;
    const uint pixelFormatFourCcFlag = 0x4;

    /// <summary>
    /// Writes a DDS file whose payload is a full mip chain of the given format.
    /// </summary>
    /// <param name="fourCc">Four-character format code, such as <c>DXT1</c> or <c>ATI2</c>.</param>
    /// <param name="width">Width of mip level 0.</param>
    /// <param name="height">Height of mip level 0.</param>
    /// <param name="mipMapCount">Value written to the header. Zero means "one level".</param>
    /// <param name="blockBytes">Bytes per 4×4 block: 8 for BC1, 16 for BC3 and BC5.</param>
    public static byte[] Build(string fourCc, uint width, uint height, uint mipMapCount, uint blockBytes)
    {
        var levels = Math.Max(1u, mipMapCount);
        var payloadLength = 0;
        for (var level = 0; level < levels; level++)
        {
            payloadLength += (int)LevelBytes(width, height, level, blockBytes);
        }

        var file = new byte[headerLength + payloadLength];
        var span = file.AsSpan();

        Write(span, 0, DdsReader.Magic);
        Write(span, 4, 124);              // dwSize
        Write(span, 12, height);
        Write(span, 16, width);
        Write(span, 28, mipMapCount);
        Write(span, 80, pixelFormatFourCcFlag);
        Write(span, 84, (uint)(fourCc[0] | (fourCc[1] << 8) | (fourCc[2] << 16) | (fourCc[3] << 24)));

        // Fill the payload with the level index so a test can tell which slice it is looking at.
        var offset = headerLength;
        for (var level = 0; level < levels; level++)
        {
            var size = (int)LevelBytes(width, height, level, blockBytes);
            span.Slice(offset, size).Fill((byte)(level + 1));
            offset += size;
        }

        return file;
    }

    /// <summary>Bytes a mip level of a block-compressed image occupies.</summary>
    /// <param name="width">Width of mip level 0.</param>
    /// <param name="height">Height of mip level 0.</param>
    /// <param name="level">Zero-based mip level.</param>
    /// <param name="blockBytes">Bytes per 4×4 block.</param>
    public static uint LevelBytes(uint width, uint height, int level, uint blockBytes)
    {
        var levelWidth = Math.Max(1u, width >> level);
        var levelHeight = Math.Max(1u, height >> level);
        return Math.Max(1u, (levelWidth + 3) / 4) * Math.Max(1u, (levelHeight + 3) / 4) * blockBytes;
    }

    static void Write(Span<byte> data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
}
