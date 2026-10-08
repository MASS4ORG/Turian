namespace Turian.Engine.Core;

public static partial class GltfModelReader
{
    readonly record struct Accessor(ReadOnlyMemory<byte> Data, int Offset, int Stride, int Count,
        int Components, int ComponentType, bool Normalized, JsonElement Description)
    {
        internal int ComponentSize => ComponentType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };

        internal ReadOnlySpan<byte> Element(int index) => Data.Span.Slice(Offset + index * Stride,
            Components * ComponentSize);

        internal Vector4 Vector(int index)
        {
            var bytes = Element(index);
            if (ComponentType == 5126 && !Normalized && BitConverter.IsLittleEndian)
                return Components switch
                {
                    1 => new Vector4(MemoryMarshal.Read<float>(bytes), 0, 0, 0),
                    2 => new Vector4(MemoryMarshal.Read<Vector2>(bytes), 0, 0),
                    3 => new Vector4(MemoryMarshal.Read<Vector3>(bytes), 0),
                    _ => MemoryMarshal.Read<Vector4>(bytes),
                };
            Span<float> values = stackalloc float[4];
            values.Clear();
            for (var component = 0; component < Components; component++)
                values[component] = Float(bytes[(component * ComponentSize)..]);
            return new(values[0], values[1], values[2], values[3]);
        }

        internal uint Index(int index) => ComponentType switch
        {
            5121 => Element(index)[0],
            5123 => BinaryPrimitives.ReadUInt16LittleEndian(Element(index)),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(Element(index)),
            _ => throw new InvalidDataException("glTF indices must be unsigned integers."),
        };

        float Float(ReadOnlySpan<byte> bytes)
        {
            var value = ComponentType switch
            {
                5120 => (sbyte)bytes[0],
                5121 => bytes[0],
                5122 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                5126 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
                _ => throw new InvalidDataException("Unsupported glTF component type."),
            };
            return Normalized ? Normalize(value) : value;
        }

        float Normalize(float value) => ComponentType switch
        {
            5120 => Math.Max(value / 127f, -1f),
            5121 => value / 255f,
            5122 => Math.Max(value / 32767f, -1f),
            5123 => value / 65535f,
            _ => throw new InvalidDataException("Unsupported normalized glTF component type."),
        };
    }

    static Accessor GetAccessor(JsonElement root, int index, ReadOnlyMemory<byte>[] buffers)
    {
        var description = root.GetProperty("accessors")[index];
        var type = description.GetProperty("componentType").GetInt32();
        var components = ComponentCount(description.GetProperty("type").GetString());
        var size = ComponentSize(type);
        var count = description.GetProperty("count").GetInt32();
        var sparse = description.TryGetProperty("sparse", out var replacements);
        ReadOnlyMemory<byte> data;
        var stride = components * size;
        if (description.TryGetProperty("bufferView", out var viewIndex))
        {
            var view = root.GetProperty("bufferViews")[viewIndex.GetInt32()];
            data = SliceView(view, buffers);
            stride = Integer(view, "byteStride", stride);
        }
        else if (sparse && count >= 0)
            data = new byte[checked(count * stride)];
        else
            throw new NotSupportedException("glTF geometry requires a buffer view or sparse values.");
        var accessor = new Accessor(data, Integer(description, "byteOffset"), stride, count,
            components, type, description.TryGetProperty("normalized", out var normalized) && normalized.GetBoolean(),
            description);
        ValidateAccessor(accessor);
        return sparse ? ExpandSparse(root, accessor, replacements, buffers) : accessor;
    }

    static int ComponentCount(string? type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        _ => throw new InvalidDataException("glTF geometry accessors must contain scalars or vectors."),
    };

    static int ComponentSize(int type) => type switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => throw new InvalidDataException("Unsupported glTF component type."),
    };

    static ReadOnlyMemory<byte> SliceView(JsonElement view, ReadOnlyMemory<byte>[] buffers)
    {
        var buffer = buffers[view.GetProperty("buffer").GetInt32()];
        var offset = Integer(view, "byteOffset");
        var length = view.GetProperty("byteLength").GetInt32();
        if (offset < 0 || length < 0 || offset > buffer.Length - length)
            throw new InvalidDataException("glTF buffer view exceeds its buffer.");
        return buffer.Slice(offset, length);
    }

    static void ValidateAccessor(Accessor accessor)
    {
        var size = accessor.Components * accessor.ComponentSize;
        var end = (long)accessor.Offset + Math.Max(0L, accessor.Count - 1L) * accessor.Stride;
        if (accessor.Count > 0)
            end += size;
        if (accessor.Count < 0 || accessor.Offset < 0 || accessor.Stride < size || end > accessor.Data.Length)
            throw new InvalidDataException("glTF accessor exceeds its buffer view.");
    }

    static Accessor? Attribute(JsonElement root, JsonElement attributes, string name,
        int components, int count, ReadOnlyMemory<byte>[] buffers)
    {
        if (!attributes.TryGetProperty(name, out var index))
            return null;
        var accessor = GetAccessor(root, index.GetInt32(), buffers);
        if (accessor.Count != count || accessor.Components != components &&
            !(name == "COLOR_0" && accessor.Components == 4))
            throw new InvalidDataException($"glTF {name} accessor has incompatible dimensions.");
        return accessor;
    }
}
