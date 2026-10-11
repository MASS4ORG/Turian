namespace Turian.Engine.Core;

public static partial class GltfModelReader
{
    static ModelBuilder Build(JsonElement root, ReadOnlyMemory<byte> binary,
        Func<int, string, byte[]>? resolver)
    {
        ValidateDocument(root);
        if (!root.TryGetProperty("meshes", out var meshes))
            return new ModelBuilder();
        var buffers = LoadBuffers(root, binary, resolver);
        var primitives = CollectPrimitives(root, meshes);

        var vertexCount = 0;
        var indexCount = 0;
        foreach (var primitive in primitives)
        {
            ValidatePrimitive(primitive);
            var position = GetAccessor(root, primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32(),
                buffers);
            vertexCount = checked(vertexCount + position.Count);
            indexCount = checked(indexCount + (primitive.TryGetProperty("indices", out var indices)
                ? GetAccessor(root, indices.GetInt32(), buffers).Count : position.Count));
        }

        var builder = new ModelBuilder { Vertices = new Vertex[vertexCount], Indices = new uint[indexCount] };
        var vertexStart = 0;
        var indexStart = 0;
        foreach (var primitive in primitives)
        {
            var count = ReadVertices(root, primitive.GetProperty("attributes"), buffers,
                builder.Vertices.AsSpan(vertexStart), out var bounds);
            var indices = ReadIndices(root, primitive, buffers, builder.Indices.AsSpan(indexStart), vertexStart, count);
            int? material = primitive.TryGetProperty("material", out var value) ? value.GetInt32() : null;
            if (indices > 0)
                builder.SubMeshes.Add(new SubMesh((uint)indexStart, (uint)indices, material, bounds));
            vertexStart += count;
            indexStart += indices;
        }
        return builder;
    }

    static List<JsonElement> CollectPrimitives(JsonElement root, JsonElement meshes)
    {
        var primitives = new List<JsonElement>();
        foreach (var meshIndex in GetMeshOrder(root))
            foreach (var primitive in meshes[meshIndex].GetProperty("primitives").EnumerateArray())
                primitives.Add(primitive);
        return primitives;
    }

    static void ValidateDocument(JsonElement root)
    {
        if (root.TryGetProperty("asset", out var asset) && asset.GetProperty("version").GetString() != "2.0")
            throw new NotSupportedException("Only glTF 2.0 models are supported.");
        if (!root.TryGetProperty("extensionsRequired", out var extensions))
            return;
        foreach (var extension in extensions.EnumerateArray())
            throw new NotSupportedException($"Required glTF extension '{extension.GetString()}' is not supported.");
    }

    static void ValidatePrimitive(JsonElement primitive)
    {
        if (Integer(primitive, "mode", 4) != 4)
            throw new NotSupportedException("Only glTF triangle primitives are supported.");
        if (primitive.TryGetProperty("extensions", out var extensions) &&
            extensions.TryGetProperty("KHR_draco_mesh_compression", out _))
            throw new NotSupportedException("Draco-compressed glTF geometry is not supported.");
    }

    static int ReadVertices(JsonElement root, JsonElement attributes, ReadOnlyMemory<byte>[] buffers,
        Span<Vertex> vertices, out Bounds bounds)
    {
        var position = GetAccessor(root, attributes.GetProperty("POSITION").GetInt32(), buffers);
        if (position.Components != 3 || position.ComponentType != 5126 || position.Normalized)
            throw new InvalidDataException("glTF POSITION must contain FLOAT VEC3 values.");
        var color = Attribute(root, attributes, "COLOR_0", 3, position.Count, buffers);
        var normal = Attribute(root, attributes, "NORMAL", 3, position.Count, buffers);
        var uv = Attribute(root, attributes, "TEXCOORD_0", 2, position.Count, buffers);
        var tangent = Attribute(root, attributes, "TANGENT", 4, position.Count, buffers);
        vertices = vertices[..position.Count];
        if (TryCopyVertices(position, color, normal, uv, tangent, vertices) ||
            TryReadFloatVertices(position, color, normal, uv, tangent, vertices))
        {
            bounds = ReadBounds(position, vertices);
            return position.Count;
        }

        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i] = ReadVertex(i, position, color, normal, uv, tangent);
            minimum = Vector3.Min(minimum, vertices[i].Position);
            maximum = Vector3.Max(maximum, vertices[i].Position);
        }
        bounds = vertices.IsEmpty ? Bounds.Empty : new Bounds(minimum, maximum);
        return position.Count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vertex ReadVertex(int index, Accessor position, Accessor? color, Accessor? normal, Accessor? uv,
        Accessor? tangent)
    {
        var pos = position.Vector(index);
        var rgb = color?.Vector(index) ?? Vector4.One;
        var norm = normal?.Vector(index) ?? new Vector4(0, 1, 0, 0);
        var tex = uv?.Vector(index) ?? Vector4.Zero;
        return new Vertex(new Vector3(pos.X, pos.Y, pos.Z), new Vector3(rgb.X, rgb.Y, rgb.Z))
        {
            Normal = new Vector3(norm.X, norm.Y, norm.Z),
            Uv = new Vector2(tex.X, tex.Y),
            Tangent = tangent?.Vector(index) ?? Vector4.Zero,
        };
    }

    static bool TryCopyVertices(Accessor position, Accessor? color, Accessor? normal, Accessor? uv,
        Accessor? tangent, Span<Vertex> vertices)
    {
        var stride = (int)Vertex.SizeOf();
        var start = position.Offset - (int)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position));
        if (!CanCopyVertexBlock(position, start, vertices.Length))
            return false;
        if (!Matches(color, position, start, nameof(Vertex.Color), 3) ||
            !Matches(normal, position, start, nameof(Vertex.Normal), 3) ||
            !Matches(uv, position, start, nameof(Vertex.Uv), 2) ||
            !Matches(tangent, position, start, nameof(Vertex.Tangent), 4))
            return false;
        MemoryMarshal.Cast<byte, Vertex>(position.Data.Span.Slice(start, vertices.Length * stride)).CopyTo(vertices);
        return true;
    }

    static bool CanCopyVertexBlock(Accessor position, int start, int count) => BitConverter.IsLittleEndian &&
        start >= 0 && position.Stride == Vertex.SizeOf() &&
        (long)start + (long)count * position.Stride <= position.Data.Length;

    static bool Matches(Accessor? candidate, Accessor position, int start, string field, int components) =>
        candidate is { } accessor && accessor.Data.Equals(position.Data) &&
        accessor.Offset == start + (int)Marshal.OffsetOf<Vertex>(field) && accessor.Stride == position.Stride &&
        accessor.ComponentType == 5126 && !accessor.Normalized && accessor.Components == components;

    static bool TryReadFloatVertices(Accessor position, Accessor? color, Accessor? normal, Accessor? uv,
        Accessor? tangent, Span<Vertex> vertices)
    {
        if (!CanReadFloatVertices(position, color, normal, uv, tangent))
            return false;
        var positions = FloatSpan<Vector3>(position);
        var colors = OptionalFloatSpan<Vector3>(color);
        var normals = OptionalFloatSpan<Vector3>(normal);
        var uvs = OptionalFloatSpan<Vector2>(uv);
        var tangents = OptionalFloatSpan<Vector4>(tangent);
        for (var i = 0; i < vertices.Length; i++)
            vertices[i] = ReadFloatVertex(i, positions, colors, normals, uvs, tangents);
        return true;
    }

    static bool CanReadFloatVertices(Accessor position, Accessor? color, Accessor? normal, Accessor? uv,
        Accessor? tangent) => BitConverter.IsLittleEndian && PackedFloat(position, 3) && PackedFloat(color, 3) &&
        PackedFloat(normal, 3) && PackedFloat(uv, 2) && PackedFloat(tangent, 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vertex ReadFloatVertex(int index, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> colors,
        ReadOnlySpan<Vector3> normals, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<Vector4> tangents) =>
        new(positions[index], colors.IsEmpty ? Vector3.One : colors[index])
        {
            Normal = normals.IsEmpty ? Vector3.UnitY : normals[index],
            Uv = uvs.IsEmpty ? Vector2.Zero : uvs[index],
            Tangent = tangents.IsEmpty ? Vector4.Zero : tangents[index],
        };

    static bool PackedFloat(Accessor? accessor, int components) =>
        accessor is not { } value || PackedFloat(value, components);

    static bool PackedFloat(Accessor accessor, int components) => accessor.ComponentType == 5126 &&
        !accessor.Normalized && accessor.Components == components && accessor.Stride == components * sizeof(float);

    static ReadOnlySpan<T> FloatSpan<T>(Accessor accessor) where T : struct =>
        MemoryMarshal.Cast<byte, T>(accessor.Data.Span.Slice(accessor.Offset, accessor.Count * accessor.Stride));

    static ReadOnlySpan<T> OptionalFloatSpan<T>(Accessor? accessor) where T : struct =>
        accessor is { } value ? FloatSpan<T>(value) : [];

    static Bounds ReadBounds(Accessor position, ReadOnlySpan<Vertex> vertices)
    {
        if (position.Description.TryGetProperty("min", out var min) &&
            position.Description.TryGetProperty("max", out var max))
            return new Bounds(new Vector3(min[0].GetSingle(), min[1].GetSingle(), min[2].GetSingle()),
                new Vector3(max[0].GetSingle(), max[1].GetSingle(), max[2].GetSingle()));
        var bounds = Bounds.Empty;
        foreach (var vertex in vertices)
            bounds = bounds.Encapsulate(vertex.Position);
        return bounds;
    }

    static int ReadIndices(JsonElement root, JsonElement primitive, ReadOnlyMemory<byte>[] buffers,
        Span<uint> indices, int vertexStart, int vertexCount)
    {
        if (!primitive.TryGetProperty("indices", out var index))
            return SequentialIndices(indices, vertexStart, vertexCount);
        var accessor = GetAccessor(root, index.GetInt32(), buffers);
        ValidateIndexAccessor(accessor);
        indices = indices[..accessor.Count];
        ValidateTriangles(indices.Length);
        CopyIndices(accessor, indices);
        OffsetIndices(indices, vertexStart, vertexCount);
        return indices.Length;
    }

    static int SequentialIndices(Span<uint> indices, int start, int count)
    {
        ValidateTriangles(count);
        for (var i = 0; i < count; i++)
            indices[i] = (uint)(start + i);
        return count;
    }

    static void ValidateIndexAccessor(Accessor accessor)
    {
        if (accessor.Components != 1 || accessor.Normalized || accessor.ComponentType is not (5121 or 5123 or 5125))
            throw new InvalidDataException("glTF indices must contain non-normalized scalar integers.");
    }

    static void CopyIndices(Accessor accessor, Span<uint> indices)
    {
        if (BitConverter.IsLittleEndian && accessor.ComponentType == 5125 && accessor.Stride == 4)
            MemoryMarshal.Cast<byte, uint>(accessor.Data.Span.Slice(accessor.Offset, accessor.Count * 4)).CopyTo(indices);
        else
            for (var i = 0; i < indices.Length; i++)
                indices[i] = accessor.Index(i);
    }

    static void OffsetIndices(Span<uint> indices, int vertexStart, int vertexCount)
    {
        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= vertexCount)
                throw new InvalidDataException("glTF index exceeds its primitive's vertex count.");
            indices[i] += (uint)vertexStart;
        }
    }

    static void ValidateTriangles(int count)
    {
        if (count % 3 != 0)
            throw new InvalidDataException("glTF triangle index count must be divisible by three.");
    }
}
