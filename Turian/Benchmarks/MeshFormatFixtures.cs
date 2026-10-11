using System.Runtime.InteropServices;
using System.Text.Json;
using Turian.Benchmarks.Legacy;

/// <summary>Creates identical uncompressed geometry in each measured container and buffer layout.</summary>
static class MeshFormatFixtures
{
    static readonly string[] names = ["POSITION", "COLOR_0", "NORMAL", "TEXCOORD_0", "TANGENT"];

    /// <summary>Writes the baseline container with one interleaved stream and one submesh.</summary>
    internal static byte[] Ammesh(Vertex[] vertices, uint[] indices)
    {
        var vertexBytes = MemoryMarshal.AsBytes(vertices.AsSpan());
        var indexBytes = MemoryMarshal.AsBytes(indices.AsSpan());
        var bounds = CalculateBounds(vertices);
        using var manifest = new MemoryStream();
        using (var writer = new BinaryWriter(manifest, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1u);
            writer.Write((uint)vertices.Length);
            writer.Write(Vertex.SizeOf());
            writer.Write(0u);
            writer.Write((uint)vertexBytes.Length);
            writer.Write((uint)LegacyAmmesh.VertexStreamAttributes.Count);
            foreach (var attribute in LegacyAmmesh.VertexStreamAttributes)
            {
                writer.Write((uint)attribute.Semantic);
                writer.Write(attribute.ComponentType);
                writer.Write(attribute.ComponentCount);
                writer.Write(attribute.ByteOffset);
            }
            writer.Write(5125u);
            writer.Write((uint)indices.Length);
            writer.Write((uint)vertexBytes.Length);
            writer.Write((uint)indexBytes.Length);
            writer.Write(1u);
            writer.Write(0u);
            writer.Write((uint)indices.Length);
            writer.Write(-1);
            WriteBounds(writer, bounds);
            writer.Write(1u);
            writer.Write(0u);
            writer.Write(1u);
            WriteBounds(writer, bounds);
            writer.Write(4u);
            writer.Write("mesh"u8);
            WriteBounds(writer, bounds);
        }
        using var container = new MemoryStream();
        using (var writer = new BinaryWriter(container, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(LegacyAmmesh.Magic);
            writer.Write(LegacyAmmesh.Version);
            writer.Write((uint)(28 + manifest.Length + vertexBytes.Length + indexBytes.Length));
            writer.Write((uint)manifest.Length);
            writer.Write(LegacyAmmesh.ManifestChunkType);
            writer.Write(manifest.GetBuffer().AsSpan(0, (int)manifest.Length));
            writer.Write((uint)(vertexBytes.Length + indexBytes.Length));
            writer.Write(LegacyAmmesh.PayloadChunkType);
            writer.Write(vertexBytes);
            writer.Write(indexBytes);
        }
        return container.ToArray();
    }

    /// <summary>Writes a glTF 2.0 binary container with either interleaved or separate attribute arrays.</summary>
    internal static byte[] Glb(Vertex[] vertices, uint[] indices, bool planar, bool omitColor = false)
    {
        var bounds = CalculateBounds(vertices);
        var views = new List<Dictionary<string, object>>();
        var accessors = new List<Dictionary<string, object>>();
        var attributes = new Dictionary<string, int>();
        using var binary = new MemoryStream();
        for (var i = 0; i < names.Length; i++)
        {
            if (omitColor && i == 1) continue;
            var attribute = LegacyAmmesh.VertexStreamAttributes[i];
            var viewIndex = planar ? views.Count : 0;
            var accessorOffset = planar ? 0 : (int)attribute.ByteOffset;
            if (planar)
            {
                var offset = (int)binary.Position;
                WriteAttribute(binary, vertices, (int)attribute.ByteOffset, (int)attribute.ComponentCount);
                views.Add(View(offset, (int)binary.Position - offset));
            }
            else if (i == 0)
            {
                binary.Write(MemoryMarshal.AsBytes(vertices.AsSpan()));
                var view = View(0, (int)binary.Length);
                view["byteStride"] = Vertex.SizeOf();
                views.Add(view);
            }
            attributes[names[i]] = accessors.Count;
            accessors.Add(new Dictionary<string, object>
            {
                ["bufferView"] = viewIndex,
                ["byteOffset"] = accessorOffset,
                ["componentType"] = 5126,
                ["count"] = vertices.Length,
                ["type"] = attribute.ComponentCount == 2 ? "VEC2" : attribute.ComponentCount == 3 ? "VEC3" : "VEC4",
            });
        }
        accessors[0]["min"] = new[] { bounds.Min.X, bounds.Min.Y, bounds.Min.Z };
        accessors[0]["max"] = new[] { bounds.Max.X, bounds.Max.Y, bounds.Max.Z };
        var indexOffset = (int)binary.Position;
        binary.Write(MemoryMarshal.AsBytes(indices.AsSpan()));
        var indexView = View(indexOffset, indices.Length * sizeof(uint));
        indexView["target"] = 34963;
        views.Add(indexView);
        accessors.Add(new Dictionary<string, object>
        {
            ["bufferView"] = views.Count - 1,
            ["componentType"] = 5125,
            ["count"] = indices.Length,
            ["type"] = "SCALAR",
        });
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = (int)binary.Length } },
            bufferViews = views,
            accessors,
            meshes = new[]
            {
                new
                {
                    name = "mesh",
                    primitives = new[]
                    {
                        new
                        {
                            attributes,
                            indices = accessors.Count - 1,
                        },
                    },
                },
            },
            nodes = new[] { new { mesh = 0 } },
            scenes = new[] { new { nodes = new[] { 0 } } },
            scene = 0,
        });
        var padding = (4 - json.Length % 4) % 4;
        using var container = new MemoryStream();
        using (var writer = new BinaryWriter(container, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x46546c67u);
            writer.Write(2u);
            writer.Write((uint)(28 + json.Length + padding + binary.Length));
            writer.Write((uint)(json.Length + padding));
            writer.Write(0x4e4f534au);
            writer.Write(json);
            for (var i = 0; i < padding; i++) writer.Write((byte)32);
            writer.Write((uint)binary.Length);
            writer.Write(0x004e4942u);
            writer.Write(binary.GetBuffer().AsSpan(0, (int)binary.Length));
        }
        return container.ToArray();
    }

    static Dictionary<string, object> View(int offset, int length) => new()
    {
        ["buffer"] = 0,
        ["byteOffset"] = offset,
        ["byteLength"] = length,
        ["target"] = 34962,
    };

    static void WriteAttribute(Stream stream, Vertex[] vertices, int offset, int components)
    {
        var bytes = MemoryMarshal.AsBytes(vertices.AsSpan());
        var stride = (int)Vertex.SizeOf();
        for (var i = 0; i < vertices.Length; i++) stream.Write(bytes.Slice(i * stride + offset, components * 4));
    }

    static Bounds CalculateBounds(Vertex[] vertices)
    {
        var bounds = Bounds.Empty;
        foreach (var vertex in vertices) bounds = bounds.Encapsulate(vertex.Position);
        return bounds;
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
}
