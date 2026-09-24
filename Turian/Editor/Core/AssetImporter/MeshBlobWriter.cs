namespace Turian.Editor.Core;

/// <summary>
/// Writes <see cref="MeshBlobContent"/> as an <c>.ammesh</c> container that
/// <see cref="MeshBlob"/> reads back.
/// </summary>
public static class MeshBlobWriter
{
    /// <summary>Writes a container to <paramref name="path"/>, replacing any existing file.</summary>
    /// <param name="path">Absolute path of the target <c>.ammesh</c> file.</param>
    /// <param name="content">The geometry to bake.</param>
    public static void Save(string path, MeshBlobContent content)
    {
        using var stream = File.Create(path);
        Write(stream, content);
    }

    /// <summary>Writes a container to <paramref name="destination"/>.</summary>
    /// <param name="destination">The stream that receives the container.</param>
    /// <param name="content">The geometry to bake.</param>
    public static void Write(Stream destination, MeshBlobContent content)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(content);

        var payload = BuildPayload(content, out var streams, out var indexByteOffset);
        var manifest = BuildManifest(content, streams, indexByteOffset);

        var totalLength = 12 + 8 + manifest.Length + 8 + payload.Length;

        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(MeshBlob.Magic);
        writer.Write(MeshBlob.Version);
        writer.Write((uint)totalLength);

        writer.Write((uint)manifest.Length);
        writer.Write(MeshBlob.ManifestChunkType);
        writer.Write(manifest);

        writer.Write((uint)payload.Length);
        writer.Write(MeshBlob.PayloadChunkType);
        writer.Write(payload);
    }

    static byte[] BuildPayload(
        MeshBlobContent content,
        out List<(IReadOnlyList<MeshBlobAttribute> Attributes, uint VertexCount, uint ByteStride, uint ByteOffset, uint ByteLength)> streams,
        out uint indexByteOffset)
    {
        streams = [];

        using var buffer = new MemoryStream();

        var vertexBytes = MemoryMarshal.AsBytes(content.Vertices.AsSpan());
        streams.Add((MeshBlob.VertexStreamAttributes, (uint)content.Vertices.Length, Vertex.SizeOf(), (uint)buffer.Position, (uint)vertexBytes.Length));
        buffer.Write(vertexBytes);
        Pad(buffer);

        if (content.TexCoord1.Length > 0)
        {
            var uvBytes = MemoryMarshal.AsBytes(content.TexCoord1.AsSpan());
            streams.Add((MeshBlob.TexCoord1StreamAttributes, (uint)content.TexCoord1.Length, sizeof(float) * 2, (uint)buffer.Position, (uint)uvBytes.Length));
            buffer.Write(uvBytes);
            Pad(buffer);
        }

        indexByteOffset = (uint)buffer.Position;
        buffer.Write(MemoryMarshal.AsBytes(content.Indices.AsSpan()));
        Pad(buffer);

        return buffer.ToArray();
    }

    static byte[] BuildManifest(
        MeshBlobContent content,
        List<(IReadOnlyList<MeshBlobAttribute> Attributes, uint VertexCount, uint ByteStride, uint ByteOffset, uint ByteLength)> streams,
        uint indexByteOffset)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        writer.Write((uint)streams.Count);
        foreach (var (attributes, vertexCount, byteStride, byteOffset, byteLength) in streams)
        {
            writer.Write(vertexCount);
            writer.Write(byteStride);
            writer.Write(byteOffset);
            writer.Write(byteLength);
            writer.Write((uint)attributes.Count);

            foreach (var attribute in attributes)
            {
                writer.Write((uint)attribute.Semantic);
                writer.Write(attribute.ComponentType);
                writer.Write(attribute.ComponentCount);
                writer.Write(attribute.ByteOffset);
            }
        }

        writer.Write(MeshBlob.ComponentTypeUnsignedInt);
        writer.Write((uint)content.Indices.Length);
        writer.Write(indexByteOffset);
        writer.Write((uint)(content.Indices.Length * sizeof(uint)));

        writer.Write((uint)content.SubMeshes.Count);
        foreach (var subMesh in content.SubMeshes)
        {
            writer.Write(subMesh.IndexStart);
            writer.Write(subMesh.IndexCount);
            writer.Write(subMesh.MaterialIndex ?? -1);
            WriteBounds(writer, subMesh.Bounds);
        }

        writer.Write((uint)content.Meshes.Count);
        foreach (var mesh in content.Meshes)
        {
            writer.Write(mesh.SubMeshStart);
            writer.Write(mesh.SubMeshCount);
            WriteBounds(writer, mesh.Bounds);
            WriteString(writer, mesh.Name);
        }

        WriteBounds(writer, content.Bounds);
        writer.Flush();

        return buffer.ToArray();
    }

    static void WriteBounds(BinaryWriter writer, Bounds bounds)
    {
        writer.Write(bounds.Min.X);
        writer.Write(bounds.Min.Y);
        writer.Write(bounds.Min.Z);
        writer.Write(bounds.Max.X);
        writer.Write(bounds.Max.Y);
        writer.Write(bounds.Max.Z);
    }

    static void WriteString(BinaryWriter writer, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
        writer.Write(new byte[MeshBlob.Padding(bytes.Length)]);
    }

    static void Pad(MemoryStream buffer)
    {
        var padding = MeshBlob.Padding((int)buffer.Position);
        for (var i = 0; i < padding; i++)
        {
            buffer.WriteByte(0);
        }
    }
}
