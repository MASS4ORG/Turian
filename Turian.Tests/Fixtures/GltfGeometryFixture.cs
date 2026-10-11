using System.Runtime.InteropServices;

namespace Turian.Tests;

/// <summary>Creates small glTF fixtures from procedural geometry for asset integration tests.</summary>
static class GltfGeometryFixture
{
    internal static void Save(string path, Vertex[] vertices, uint[] indices)
    {
        var vertexBytes = MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray();
        var indexBytes = MemoryMarshal.AsBytes(indices.AsSpan()).ToArray();
        var bounds = Bounds.Empty;
        foreach (var vertex in vertices) bounds = bounds.Encapsulate(vertex.Position);
        var json = new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = vertexBytes.Length + indexBytes.Length } },
            bufferViews = new object[]
            {
                new { buffer = 0, byteLength = vertexBytes.Length, byteStride = Vertex.SizeOf() },
                new { buffer = 0, byteOffset = vertexBytes.Length, byteLength = indexBytes.Length },
            },
            accessors = new object[]
            {
                new { bufferView = 0, componentType = 5126, count = vertices.Length, type = "VEC3",
                    min = new[] { bounds.Min.X, bounds.Min.Y, bounds.Min.Z },
                    max = new[] { bounds.Max.X, bounds.Max.Y, bounds.Max.Z } },
                Attribute("Color", "VEC3", vertices.Length),
                Attribute("Normal", "VEC3", vertices.Length),
                Attribute("Uv", "VEC2", vertices.Length),
                Attribute("Tangent", "VEC4", vertices.Length),
                new { bufferView = 1, componentType = 5125, count = indices.Length, type = "SCALAR" },
            },
            meshes = new[] { new { primitives = new[] { new
            {
                attributes = new { POSITION = 0, COLOR_0 = 1, NORMAL = 2, TEXCOORD_0 = 3, TANGENT = 4 },
                indices = 5,
            } } } },
        };
        var text = JsonSerializer.SerializeToUtf8Bytes(json);
        var padded = (text.Length + 3) & ~3;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x46546c67u);
        writer.Write(2u);
        writer.Write((uint)(28 + padded + vertexBytes.Length + indexBytes.Length));
        writer.Write((uint)padded);
        writer.Write(0x4e4f534au);
        writer.Write(text);
        for (var i = text.Length; i < padded; i++) writer.Write((byte)' ');
        writer.Write((uint)(vertexBytes.Length + indexBytes.Length));
        writer.Write(0x004e4942u);
        writer.Write(vertexBytes);
        writer.Write(indexBytes);
    }

    static object Attribute(string field, string type, int count) => new
    {
        bufferView = 0,
        byteOffset = (int)Marshal.OffsetOf<Vertex>(field),
        componentType = 5126,
        count,
        type,
    };
}
