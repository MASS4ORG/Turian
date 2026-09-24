namespace Turian.Editor.Core;

/// <summary>
/// Writes <see cref="TextureBlobContent"/> as an <c>.amtex</c> container that
/// <see cref="TextureBlob"/> reads back.
/// </summary>
public static class TextureBlobWriter
{
    /// <summary>Writes a container to <paramref name="path"/>, replacing any existing file.</summary>
    /// <param name="path">Absolute path of the target <c>.amtex</c> file.</param>
    /// <param name="content">The texture data to bake.</param>
    public static void Save(string path, TextureBlobContent content)
    {
        using var stream = File.Create(path);
        Write(stream, content);
    }

    /// <summary>Writes a container to <paramref name="destination"/>.</summary>
    /// <param name="destination">The stream that receives the container.</param>
    /// <param name="content">The texture data to bake.</param>
    public static void Write(Stream destination, TextureBlobContent content)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(content);
        if (content.Levels.Count == 0)
        {
            throw new ArgumentException("A texture blob needs at least one mip level", nameof(content));
        }

        var payload = BuildPayload(content, out var ranges);
        var manifest = BuildManifest(content, ranges);

        var totalLength = 12 + 8 + manifest.Length + 8 + payload.Length;

        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(TextureBlob.Magic);
        writer.Write(TextureBlob.Version);
        writer.Write((uint)totalLength);

        writer.Write((uint)manifest.Length);
        writer.Write(TextureBlob.ManifestChunkType);
        writer.Write(manifest);

        writer.Write((uint)payload.Length);
        writer.Write(TextureBlob.PayloadChunkType);
        writer.Write(payload);
    }

    /// <summary>
    /// Packs the levels back to back. They stay tightly packed so each offset is already a multiple
    /// of the format's element size, which is what a buffer-to-image copy requires.
    /// </summary>
    static byte[] BuildPayload(TextureBlobContent content, out List<(uint Offset, uint Length)> ranges)
    {
        ranges = [];

        using var buffer = new MemoryStream();
        foreach (var level in content.Levels)
        {
            ranges.Add(((uint)buffer.Position, (uint)level.Length));
            buffer.Write(level.Span);
        }

        return buffer.ToArray();
    }

    static byte[] BuildManifest(TextureBlobContent content, List<(uint Offset, uint Length)> ranges)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        writer.Write((uint)content.Format);
        writer.Write(content.Width);
        writer.Write(content.Height);
        writer.Write(content.IsSrgb ? 1u : 0u);
        writer.Write((uint)ranges.Count);

        foreach (var (offset, length) in ranges)
        {
            writer.Write(offset);
            writer.Write(length);
        }

        writer.Flush();
        return buffer.ToArray();
    }
}
