namespace Turian.Engine.Core;

/// <summary>
/// Reads the <c>.ammesh</c> binary mesh container: a 12-byte header followed by a
/// binary manifest chunk and a payload chunk holding the vertex streams and the
/// index buffer. Attribute semantics and component types are glTF 2.0's.
/// </summary>
public sealed class MeshBlob
{
    /// <summary>File extension of the container.</summary>
    public const string FileExtension = ".ammesh";

    /// <summary>Header magic, the ASCII bytes <c>AMSH</c>.</summary>
    public const uint Magic = 0x48534D41;

    /// <summary>Container version this reader accepts.</summary>
    public const uint Version = 1;

    /// <summary>Chunk type of the binary manifest, the ASCII bytes <c>MANI</c>.</summary>
    public const uint ManifestChunkType = 0x494E414D;

    /// <summary>Chunk type of the payload, the ASCII bytes <c>BIN\0</c>.</summary>
    public const uint PayloadChunkType = 0x004E4942;

    /// <summary>glTF 2.0 component type for 32-bit floats.</summary>
    public const uint ComponentTypeFloat = 5126;

    /// <summary>glTF 2.0 component type for 32-bit unsigned integers.</summary>
    public const uint ComponentTypeUnsignedInt = 5125;

    /// <summary>
    /// Layout of the interleaved <see cref="Vertex"/> stream, which is always stream 0.
    /// </summary>
    public static IReadOnlyList<MeshBlobAttribute> VertexStreamAttributes { get; } =
    [
        new(MeshAttributeSemantic.Position, ComponentTypeFloat, 3, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position))),
        new(MeshAttributeSemantic.Color0, ComponentTypeFloat, 3, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Color))),
        new(MeshAttributeSemantic.Normal, ComponentTypeFloat, 3, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Normal))),
        new(MeshAttributeSemantic.TexCoord0, ComponentTypeFloat, 2, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Uv))),
        new(MeshAttributeSemantic.Tangent, ComponentTypeFloat, 4, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Tangent))),
    ];

    /// <summary>
    /// Layout of the optional secondary UV stream, which is always stream 1 when present.
    /// </summary>
    public static IReadOnlyList<MeshBlobAttribute> TexCoord1StreamAttributes { get; } =
    [
        new(MeshAttributeSemantic.TexCoord1, ComponentTypeFloat, 2, 0),
    ];

    /// <summary>The stream descriptors as they were written.</summary>
    public IReadOnlyList<MeshBlobStream> Streams { get; private init; } = [];

    /// <summary>The interleaved vertices decoded from stream 0.</summary>
    public Vertex[] Vertices { get; private init; } = [];

    /// <summary>The secondary UV set decoded from stream 1, empty when the file has none.</summary>
    public Vector2[] TexCoord1 { get; private init; } = [];

    /// <summary>The shared index buffer every submesh addresses.</summary>
    public uint[] Indices { get; private init; } = [];

    /// <summary>The submesh table.</summary>
    public IReadOnlyList<SubMesh> SubMeshes { get; private init; } = [];

    /// <summary>The mesh table; one entry per <see cref="MeshAsset"/> the file produces.</summary>
    public IReadOnlyList<MeshBlobMesh> Meshes { get; private init; } = [];

    /// <summary>Axis-aligned bounds covering every mesh in the file.</summary>
    public Bounds Bounds { get; private init; }

    /// <summary>Reads a mesh blob from a file.</summary>
    /// <param name="path">Absolute path of the <c>.ammesh</c> file.</param>
    public static MeshBlob Load(string path) => Read(File.ReadAllBytes(path));

    /// <summary>Reads a mesh blob from a stream, consuming it to the end.</summary>
    /// <param name="stream">The stream positioned at the container header.</param>
    public static MeshBlob Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>Reads a mesh blob from an in-memory container.</summary>
    /// <param name="data">The complete container bytes.</param>
    public static MeshBlob Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            throw new InvalidDataException("Mesh blob is shorter than its header.");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != Magic)
        {
            throw new InvalidDataException("Mesh blob magic does not match.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported mesh blob version {version}.");
        }

        var totalLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (totalLength > data.Length)
        {
            throw new InvalidDataException("Mesh blob is truncated.");
        }

        ReadOnlySpan<byte> manifest = default;
        ReadOnlySpan<byte> payload = default;

        var cursor = 12;
        while (cursor + 8 <= totalLength)
        {
            var chunkLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 4)..]);
            cursor += 8;

            if (cursor + chunkLength > totalLength)
            {
                throw new InvalidDataException("Mesh blob chunk runs past the end of the file.");
            }

            var chunk = data.Slice(cursor, chunkLength);
            if (chunkType == ManifestChunkType)
            {
                manifest = chunk;
            }
            else if (chunkType == PayloadChunkType)
            {
                payload = chunk;
            }

            cursor += chunkLength;
        }

        if (manifest.IsEmpty)
        {
            throw new InvalidDataException("Mesh blob has no manifest chunk.");
        }

        return ReadManifest(manifest, payload);
    }

    /// <summary>
    /// Projects the blob onto a <see cref="ModelBuilder"/> so it can be uploaded as a
    /// single <see cref="Model"/> sharing one vertex buffer and one index buffer.
    /// </summary>
    public ModelBuilder ToModelBuilder() => new()
    {
        Vertices = Vertices,
        Indices = Indices,
        SubMeshes = [.. SubMeshes],
    };

    static MeshBlob ReadManifest(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> payload)
    {
        var cursor = 0;

        var streamCount = ReadUInt32(manifest, ref cursor);
        var streams = new MeshBlobStream[streamCount];
        var streamRanges = new (uint Offset, uint Length)[streamCount];

        for (var i = 0; i < streamCount; i++)
        {
            var vertexCount = ReadUInt32(manifest, ref cursor);
            var byteStride = ReadUInt32(manifest, ref cursor);
            var byteOffset = ReadUInt32(manifest, ref cursor);
            var byteLength = ReadUInt32(manifest, ref cursor);
            var attributeCount = ReadUInt32(manifest, ref cursor);

            var attributes = new MeshBlobAttribute[attributeCount];
            for (var a = 0; a < attributeCount; a++)
            {
                attributes[a] = new MeshBlobAttribute(
                    (MeshAttributeSemantic)ReadUInt32(manifest, ref cursor),
                    ReadUInt32(manifest, ref cursor),
                    ReadUInt32(manifest, ref cursor),
                    ReadUInt32(manifest, ref cursor));
            }

            streams[i] = new MeshBlobStream(vertexCount, byteStride, attributes);
            streamRanges[i] = (byteOffset, byteLength);
        }

        _ = ReadUInt32(manifest, ref cursor); // index component type; always ComponentTypeUnsignedInt
        var indexCount = ReadUInt32(manifest, ref cursor);
        var indexByteOffset = ReadUInt32(manifest, ref cursor);
        _ = ReadUInt32(manifest, ref cursor); // index byte length, derivable from the count

        var subMeshCount = ReadUInt32(manifest, ref cursor);
        var subMeshes = new SubMesh[subMeshCount];
        for (var i = 0; i < subMeshCount; i++)
        {
            var indexStart = ReadUInt32(manifest, ref cursor);
            var subMeshIndexCount = ReadUInt32(manifest, ref cursor);
            var materialIndex = (int)ReadUInt32(manifest, ref cursor);
            var bounds = ReadBounds(manifest, ref cursor);
            subMeshes[i] = new SubMesh(
                indexStart,
                subMeshIndexCount,
                materialIndex < 0 ? null : materialIndex,
                bounds);
        }

        var meshCount = ReadUInt32(manifest, ref cursor);
        var meshes = new MeshBlobMesh[meshCount];
        for (var i = 0; i < meshCount; i++)
        {
            var subMeshStart = ReadUInt32(manifest, ref cursor);
            var meshSubMeshCount = ReadUInt32(manifest, ref cursor);
            var bounds = ReadBounds(manifest, ref cursor);
            meshes[i] = new MeshBlobMesh(ReadString(manifest, ref cursor), subMeshStart, meshSubMeshCount, bounds);
        }

        var fileBounds = ReadBounds(manifest, ref cursor);

        var vertices = Array.Empty<Vertex>();
        var texCoord1 = Array.Empty<Vector2>();

        for (var i = 0; i < streams.Length; i++)
        {
            var (offset, length) = streamRanges[i];
            var slice = payload.Slice((int)offset, (int)length);

            if (streams[i].Attributes.Any(static attribute => attribute.Semantic == MeshAttributeSemantic.Position))
            {
                vertices = DecodeVertices(streams[i], slice);
            }
            else if (streams[i].Attributes.Any(static attribute => attribute.Semantic == MeshAttributeSemantic.TexCoord1))
            {
                texCoord1 = [.. MemoryMarshal.Cast<byte, Vector2>(slice)];
            }
        }

        var indices = indexCount == 0
            ? []
            : MemoryMarshal.Cast<byte, uint>(payload.Slice((int)indexByteOffset, (int)(indexCount * sizeof(uint)))).ToArray();

        return new MeshBlob
        {
            Streams = streams,
            Vertices = vertices,
            TexCoord1 = texCoord1,
            Indices = indices,
            SubMeshes = subMeshes,
            Meshes = meshes,
            Bounds = fileBounds,
        };
    }

    static Vertex[] DecodeVertices(MeshBlobStream stream, ReadOnlySpan<byte> data)
    {
        var count = (int)stream.VertexCount;
        if (count == 0)
        {
            return [];
        }

        if (stream.ByteStride == Vertex.SizeOf() && stream.Attributes.SequenceEqual(VertexStreamAttributes))
        {
            return [.. MemoryMarshal.Cast<byte, Vertex>(data[..(count * (int)stream.ByteStride)])];
        }

        var vertices = new Vertex[count];
        Span<float> components = stackalloc float[4];

        foreach (var attribute in stream.Attributes)
        {
            if (attribute.ComponentType != ComponentTypeFloat)
            {
                continue;
            }

            for (var i = 0; i < count; i++)
            {
                var elementOffset = i * (int)stream.ByteStride + (int)attribute.ByteOffset;
                for (var c = 0; c < attribute.ComponentCount; c++)
                {
                    components[c] = BinaryPrimitives.ReadSingleLittleEndian(data[(elementOffset + c * 4)..]);
                }

                Assign(ref vertices[i], attribute.Semantic, components);
            }
        }

        return vertices;
    }

    static void Assign(ref Vertex vertex, MeshAttributeSemantic semantic, ReadOnlySpan<float> components)
    {
        switch (semantic)
        {
            case MeshAttributeSemantic.Position:
                vertex.Position = new Vector3(components[0], components[1], components[2]);
                break;
            case MeshAttributeSemantic.Normal:
                vertex.Normal = new Vector3(components[0], components[1], components[2]);
                break;
            case MeshAttributeSemantic.Color0:
                vertex.Color = new Vector3(components[0], components[1], components[2]);
                break;
            case MeshAttributeSemantic.TexCoord0:
                vertex.Uv = new Vector2(components[0], components[1]);
                break;
            case MeshAttributeSemantic.Tangent:
                vertex.Tangent = new Vector4(components[0], components[1], components[2], components[3]);
                break;
        }
    }

    static uint ReadUInt32(ReadOnlySpan<byte> data, ref int cursor)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
        cursor += 4;
        return value;
    }

    static float ReadSingle(ReadOnlySpan<byte> data, ref int cursor)
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(data[cursor..]);
        cursor += 4;
        return value;
    }

    static Bounds ReadBounds(ReadOnlySpan<byte> data, ref int cursor)
    {
        var min = new Vector3(ReadSingle(data, ref cursor), ReadSingle(data, ref cursor), ReadSingle(data, ref cursor));
        var max = new Vector3(ReadSingle(data, ref cursor), ReadSingle(data, ref cursor), ReadSingle(data, ref cursor));
        return new Bounds(min, max);
    }

    static string ReadString(ReadOnlySpan<byte> data, ref int cursor)
    {
        var byteLength = (int)ReadUInt32(data, ref cursor);
        var value = Encoding.UTF8.GetString(data.Slice(cursor, byteLength));
        cursor += byteLength + Padding(byteLength);
        return value;
    }

    /// <summary>
    /// Returns the number of bytes needed after <paramref name="length"/> to reach a
    /// 4-byte boundary.
    /// </summary>
    /// <param name="length">Length of the preceding run of bytes.</param>
    public static int Padding(int length) => (4 - (length & 3)) & 3;
}
