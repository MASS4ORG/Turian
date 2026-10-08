namespace Turian.Engine.Core;

/// <summary>Reads triangle geometry from glTF 2.0 JSON or GLB using the source buffer layouts.</summary>
public static partial class GltfModelReader
{
    const uint GlbMagic = 0x46546c67;
    const uint JsonChunk = 0x4e4f534a;
    const uint BinaryChunk = 0x004e4942;

    /// <summary>Loads a glTF model and resolves external buffers relative to its file.</summary>
    public static ModelBuilder Load(string path)
    {
        using var stream = File.OpenRead(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Read(stream, (_, uri) => File.ReadAllBytes(Path.Combine(directory, Uri.UnescapeDataString(uri))));
    }

    /// <summary>Reads geometry from a stream, optionally resolving external buffer URIs.</summary>
    public static ModelBuilder Read(Stream stream, Func<int, string, byte[]>? resolveExternalBuffer = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
        {
            var data = GC.AllocateUninitializedArray<byte>(checked((int)(stream.Length - stream.Position)));
            stream.ReadExactly(data);
            return Read(data, resolveExternalBuffer);
        }
        using var storage = new MemoryStream();
        stream.CopyTo(storage);
        return Read(storage.GetBuffer().AsMemory(0, checked((int)storage.Length)), resolveExternalBuffer);
    }

    /// <summary>Reads geometry from UTF-8 JSON or GLB bytes, optionally resolving external buffer URIs.</summary>
    public static ModelBuilder Read(ReadOnlyMemory<byte> data,
        Func<int, string, byte[]>? resolveExternalBuffer = null)
    {
        var (json, binary) = SplitContainer(data);
        using var document = JsonDocument.Parse(json);
        return Build(document.RootElement, binary, resolveExternalBuffer);
    }

    internal static ModelBuilder ReadJson(string json, byte[]? embeddedBinary,
        Func<int, string, byte[]>? resolveExternalBuffer = null)
    {
        using var document = JsonDocument.Parse(json);
        return Build(document.RootElement, embeddedBinary ?? [], resolveExternalBuffer);
    }

    static (ReadOnlyMemory<byte> Json, ReadOnlyMemory<byte> Binary) SplitContainer(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(data.Span) != GlbMagic)
            return (data, default);
        ValidateGlbHeader(data);
        return ReadChunks(data);
    }

    static void ValidateGlbHeader(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data.Span[4..]) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.Span[8..]) != data.Length)
            throw new InvalidDataException("Invalid glTF 2.0 binary header.");
    }

    static (ReadOnlyMemory<byte> Json, ReadOnlyMemory<byte> Binary) ReadChunks(ReadOnlyMemory<byte> data)
    {
        ReadOnlyMemory<byte> json = default;
        ReadOnlyMemory<byte> binary = default;
        var offset = 12;
        while (offset < data.Length)
        {
            var (type, chunk) = ReadChunk(data, offset);
            switch (type)
            {
                case JsonChunk when json.IsEmpty: json = chunk; break;
                case BinaryChunk when binary.IsEmpty: binary = chunk; break;
            }
            offset += 8 + chunk.Length;
        }
        return (json, binary);
    }

    static (uint Type, ReadOnlyMemory<byte> Data) ReadChunk(ReadOnlyMemory<byte> data, int offset)
    {
        if (data.Length - offset < 8)
            throw new InvalidDataException("Truncated GLB chunk header.");
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Span[offset..]));
        var type = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[(offset + 4)..]);
        if (length % 4 != 0 || length > data.Length - offset - 8)
            throw new InvalidDataException("Invalid GLB chunk length.");
        if (offset == 12 && type != JsonChunk)
            throw new InvalidDataException("The first GLB chunk must contain JSON.");
        return (type, data.Slice(offset + 8, length));
    }

    static ReadOnlyMemory<byte>[] LoadBuffers(JsonElement root, ReadOnlyMemory<byte> binary,
        Func<int, string, byte[]>? resolver)
    {
        if (!root.TryGetProperty("buffers", out var descriptions))
            return [];
        var buffers = new ReadOnlyMemory<byte>[descriptions.GetArrayLength()];
        for (var i = 0; i < buffers.Length; i++)
        {
            var description = descriptions[i];
            var data = description.TryGetProperty("uri", out var uri)
                ? ResolveBuffer(i, uri.GetString()!, resolver) : binary;
            var length = description.GetProperty("byteLength").GetInt32();
            if (length < 0 || data.Length < length)
                throw new InvalidDataException($"glTF buffer {i} is shorter than its declared length.");
            buffers[i] = data[..length];
        }
        return buffers;
    }

    static ReadOnlyMemory<byte> ResolveBuffer(int index, string uri, Func<int, string, byte[]>? resolver)
    {
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return resolver?.Invoke(index, uri)
                ?? throw new InvalidDataException($"External glTF buffer '{uri}' requires a resolver.");
        var comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("glTF data buffers must use base64 encoding.");
        return Convert.FromBase64String(uri[(comma + 1)..]);
    }

    static int Integer(JsonElement element, string property, int fallback = 0) =>
        element.TryGetProperty(property, out var value) ? value.GetInt32() : fallback;
}
