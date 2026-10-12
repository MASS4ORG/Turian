namespace Turian.Engine.Core;

/// <summary>
/// A 2D texture as a KTX2 container holds it: one <see cref="Ktx2VkFormat"/>, the base extent, the mip levels in
/// the layout the GPU samples and the key/value metadata. <see cref="Ktx2Reader"/> produces it and
/// <see cref="Ktx2Writer"/> consumes it.
/// </summary>
public sealed record Ktx2Texture
{
    /// <summary>The key of the standard entry naming the tool that wrote the file.</summary>
    public const string WriterKey = "KTXwriter";

    /// <summary>Format of the stored levels.</summary>
    public required Ktx2VkFormat Format { get; init; }

    /// <summary>Width of mip level 0 in texels.</summary>
    public required uint Width { get; init; }

    /// <summary>Height of mip level 0 in texels.</summary>
    public required uint Height { get; init; }

    /// <summary>The mip levels, largest first, tightly packed. At least one.</summary>
    public required IReadOnlyList<ReadOnlyMemory<byte>> Levels { get; init; }

    /// <summary>
    /// Key/value metadata. Values are raw bytes; text values end with a NUL byte. Engine data uses keys namespaced
    /// with the <c>Turian:</c> prefix, while keys starting with <c>KTX</c> are reserved by the specification.
    /// </summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> KeyValues { get; init; } =
        new Dictionary<string, ReadOnlyMemory<byte>>();

    /// <summary>
    /// The data format descriptor as read from a file, including its total-size word; empty for a texture built in
    /// memory, which the writer describes from <see cref="Format"/>.
    /// </summary>
    public ReadOnlyMemory<byte> DataFormatDescriptor { get; init; }

    /// <summary>Whether <see cref="Format"/> stores color in sRGB space.</summary>
    public bool IsSrgb => Ktx2DataFormat.IsSupported(Format) && Ktx2DataFormat.IsSrgb(Format);

    /// <summary>Encodes text as a key/value entry value: UTF-8 followed by a NUL byte.</summary>
    /// <param name="text">The text to encode.</param>
    public static ReadOnlyMemory<byte> TextValue(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    /// <summary>Reads a text entry, dropping its trailing NUL.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>The text, or <c>null</c> when the entry is missing.</returns>
    public string? GetText(string key)
    {
        if (!KeyValues.TryGetValue(key, out var value)) return null;

        var span = value.Span;
        if (span.Length > 0 && span[^1] == 0) span = span[..^1];
        return Encoding.UTF8.GetString(span);
    }
}
