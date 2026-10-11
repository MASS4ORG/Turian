namespace Turian.Engine.Core;

public static partial class GltfModelReader
{
    static Accessor ExpandSparse(JsonElement root, Accessor original, JsonElement sparse,
        ReadOnlyMemory<byte>[] buffers)
    {
        var count = sparse.GetProperty("count").GetInt32();
        if (count < 1 || count > original.Count)
            throw new InvalidDataException("Invalid glTF sparse accessor count.");
        var indexDescription = sparse.GetProperty("indices");
        var type = indexDescription.GetProperty("componentType").GetInt32();
        if (type is not (5121 or 5123 or 5125))
            throw new InvalidDataException("glTF sparse indices must contain unsigned integers.");
        var indices = SparseAccessor(root, indexDescription, type, 1, count, buffers);
        var values = SparseAccessor(root, sparse.GetProperty("values"), original.ComponentType,
            original.Components, count, buffers);
        var elementSize = original.Components * original.ComponentSize;
        var dense = new byte[checked(original.Count * elementSize)];
        CopyDenseBase(original, dense, elementSize);
        ApplySparse(indices, values, dense, original.Count, elementSize);
        return original with { Data = dense, Offset = 0, Stride = elementSize };
    }

    static void CopyDenseBase(Accessor original, Span<byte> dense, int elementSize)
    {
        for (var i = 0; i < original.Count; i++)
            original.Element(i).CopyTo(dense[(i * elementSize)..]);
    }

    static void ApplySparse(Accessor indices, Accessor values, Span<byte> dense, int count, int elementSize)
    {
        uint previous = 0;
        for (var i = 0; i < indices.Count; i++)
        {
            var index = indices.Index(i);
            if (index >= count || i > 0 && index <= previous)
                throw new InvalidDataException("glTF sparse indices must be increasing and within the accessor.");
            values.Element(i).CopyTo(dense[checked((int)index * elementSize)..]);
            previous = index;
        }
    }

    static Accessor SparseAccessor(JsonElement root, JsonElement description, int type, int components,
        int count, ReadOnlyMemory<byte>[] buffers)
    {
        var view = root.GetProperty("bufferViews")[description.GetProperty("bufferView").GetInt32()];
        var accessor = new Accessor(SliceView(view, buffers), Integer(description, "byteOffset"),
            components * ComponentSize(type), count, components, type, false, default);
        ValidateAccessor(accessor);
        return accessor;
    }
}
