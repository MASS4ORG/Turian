namespace Turian.Engine.Core;

/// <summary>
/// Writes a <see cref="Ktx2Texture"/> as a KTX2 container without supercompression: the descriptor comes from
/// <see cref="Ktx2DataFormat"/>, key/value entries are sorted, and levels are stored smallest first and aligned as
/// the specification requires, so the file passes <c>ktx validate</c>.
/// </summary>
public static class Ktx2Writer
{
    /// <summary>The <see cref="Ktx2Texture.WriterKey"/> value written when the texture names no writer.</summary>
    public const string WriterName = "Turian";

    /// <summary>Every format this writer emits stores bytes, which KTX2 records as a type size of one.</summary>
    const uint typeSize = 1;

    /// <summary>Writes a container to <paramref name="path"/>, replacing any existing file.</summary>
    /// <param name="path">Path of the target <c>.ktx2</c> file.</param>
    /// <param name="texture">The texture to write.</param>
    public static void Save(string path, Ktx2Texture texture)
    {
        using var stream = File.Create(path);
        Write(stream, texture);
    }

    /// <summary>Writes a container to <paramref name="destination"/>.</summary>
    /// <param name="destination">The stream that receives the container.</param>
    /// <param name="texture">The texture to write.</param>
    /// <exception cref="ArgumentException">The texture has no levels or a level has the wrong size.</exception>
    /// <exception cref="NotSupportedException">The format has no data format descriptor.</exception>
    public static void Write(Stream destination, Ktx2Texture texture)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(texture);

        var descriptor = Ktx2DataFormat.Build(texture.Format);
        ValidateLevels(texture);

        var keyValues = BuildKeyValues(texture.KeyValues);
        var levelCount = texture.Levels.Count;
        var descriptorOffset = Ktx2Reader.HeaderLength + (levelCount * Ktx2Reader.LevelEntryLength);
        var keyValueOffset = descriptorOffset + descriptor.Length;
        var head = new byte[keyValueOffset + keyValues.Length];
        var offsets = LayoutLevels(texture, (ulong)head.Length);

        var span = head.AsSpan();
        Ktx2Reader.Identifier.CopyTo(span);
        WriteU32(span, 12, (uint)texture.Format);
        WriteU32(span, 16, typeSize);
        WriteU32(span, 20, texture.Width);
        WriteU32(span, 24, texture.Height);
        WriteU32(span, 36, 1);
        WriteU32(span, 40, (uint)levelCount);
        WriteU32(span, 48, (uint)descriptorOffset);
        WriteU32(span, 52, (uint)descriptor.Length);
        WriteU32(span, 56, (uint)keyValueOffset);
        WriteU32(span, 60, (uint)keyValues.Length);

        for (var level = 0; level < levelCount; level++)
        {
            var entry = span[(Ktx2Reader.HeaderLength + (level * Ktx2Reader.LevelEntryLength))..];
            var length = (ulong)texture.Levels[level].Length;
            BinaryPrimitives.WriteUInt64LittleEndian(entry, offsets[level]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], length);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], length);
        }

        descriptor.CopyTo(span[descriptorOffset..]);
        keyValues.CopyTo(span[keyValueOffset..]);
        WriteBody(destination, head, texture.Levels, offsets);
    }

    static void ValidateLevels(Ktx2Texture texture)
    {
        if (texture.Levels.Count == 0)
            throw new ArgumentException("A KTX2 texture needs at least one mip level.", nameof(texture));

        for (var level = 0; level < texture.Levels.Count; level++)
        {
            var expected = Ktx2DataFormat.ImageSize(texture.Format, texture.Width >> level, texture.Height >> level);
            if ((ulong)texture.Levels[level].Length != expected)
            {
                throw new ArgumentException(
                    $"Mip level {level} holds {texture.Levels[level].Length} bytes; {texture.Format} needs {expected}.",
                    nameof(texture));
            }
        }
    }

    /// <summary>
    /// Places the levels after the header, smallest first, each starting on a multiple of the least common multiple
    /// of the texel block size and four, as KTX2 requires for uncompressed payloads.
    /// </summary>
    static ulong[] LayoutLevels(Ktx2Texture texture, ulong start)
    {
        var alignment = LeastCommonMultiple(Ktx2DataFormat.BlockShape(texture.Format).Bytes, 4);
        var offsets = new ulong[texture.Levels.Count];
        var cursor = start;
        for (var level = offsets.Length - 1; level >= 0; level--)
        {
            cursor = AlignUp(cursor, alignment);
            offsets[level] = cursor;
            cursor += (ulong)texture.Levels[level].Length;
        }

        return offsets;
    }

    static void WriteBody(Stream destination, byte[] head, IReadOnlyList<ReadOnlyMemory<byte>> levels, ulong[] offsets)
    {
        destination.Write(head);
        var position = (ulong)head.Length;
        Span<byte> padding = stackalloc byte[16];
        for (var level = levels.Count - 1; level >= 0; level--)
        {
            destination.Write(padding[..(int)(offsets[level] - position)]);
            destination.Write(levels[level].Span);
            position = offsets[level] + (ulong)levels[level].Length;
        }
    }

    /// <summary>Encodes the entries sorted by their UTF-8 key bytes, each padded to four bytes.</summary>
    static byte[] BuildKeyValues(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> keyValues)
    {
        var entries = keyValues.Select(pair => (Key: Encoding.UTF8.GetBytes(pair.Key), pair.Value)).ToList();
        if (!keyValues.ContainsKey(Ktx2Texture.WriterKey))
        {
            entries.Add((Encoding.UTF8.GetBytes(Ktx2Texture.WriterKey), Ktx2Texture.TextValue(WriterName)));
        }

        entries.Sort((left, right) => left.Key.AsSpan().SequenceCompareTo(right.Key));

        using var buffer = new MemoryStream();
        Span<byte> word = stackalloc byte[4];
        foreach (var (key, value) in entries)
        {
            if (key.Length == 0 || key.Contains((byte)0))
                throw new ArgumentException("KTX2 keys must be non-empty and contain no NUL character.");

            var length = key.Length + 1 + value.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)length);
            buffer.Write(word);
            buffer.Write(key);
            buffer.WriteByte(0);
            buffer.Write(value.Span);
            buffer.Write(new byte[AlignUp((ulong)length, 4) - (ulong)length]);
        }

        return buffer.ToArray();
    }

    static ulong AlignUp(ulong value, uint alignment) => (value + alignment - 1) / alignment * alignment;

    static uint LeastCommonMultiple(uint left, uint right)
    {
        var (a, b) = (left, right);
        while (b != 0) (a, b) = (b, a % b);
        return left / a * right;
    }

    static void WriteU32(Span<byte> data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
}
