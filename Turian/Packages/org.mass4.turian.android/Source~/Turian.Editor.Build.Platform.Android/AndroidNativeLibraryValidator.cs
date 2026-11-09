using System.Buffers.Binary;

namespace Turian.Editor.Core;

/// <summary>Checks native load segments against Android's 16 KB page size.</summary>
static class AndroidNativeLibraryValidator
{
    internal static void Validate(Stream stream, string name)
    {
        Span<byte> header = stackalloc byte[64];
        stream.ReadExactly(header);
        ValidateImageHeader(header, name);
        var (table, size, count) = ReadProgramTable(header, stream.Length, name);
        Span<byte> segment = stackalloc byte[56];
        var loadCount = 0;
        for (var index = 0; index < count; index++)
        {
            stream.Position = checked((long)(table + (ulong)index * size));
            stream.ReadExactly(segment);
            if (ValidateLoadSegment(segment, name))
            {
                loadCount++;
            }
        }
        if (loadCount == 0)
        {
            throw new InvalidDataException($"APK native library '{name}' has no ELF load segments.");
        }
    }

    static void ValidateImageHeader(ReadOnlySpan<byte> header, string name)
    {
        if (!header[..6].SequenceEqual("\u007fELF\u0002\u0001"u8))
        {
            throw new InvalidDataException($"APK native library '{name}' must be a little-endian ELF64 image.");
        }
    }

    static (ulong Offset, ushort EntrySize, ushort Count) ReadProgramTable(
        ReadOnlySpan<byte> header, long length, string name)
    {
        var table = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
        var size = BinaryPrimitives.ReadUInt16LittleEndian(header[54..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header[56..]);
        if (size < 56 || count == 0 || table > (ulong)length
            || (ulong)count * size > (ulong)length - table)
        {
            throw new InvalidDataException($"APK native library '{name}' has an invalid ELF program table.");
        }
        return (table, size, count);
    }

    static bool ValidateLoadSegment(ReadOnlySpan<byte> segment, string name)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(segment) != 1)
        {
            return false;
        }
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(segment[8..]);
        var address = BinaryPrimitives.ReadUInt64LittleEndian(segment[16..]);
        var alignment = BinaryPrimitives.ReadUInt64LittleEndian(segment[48..]);
        if (alignment < 16384 || (alignment & (alignment - 1)) != 0 || offset % 16384 != address % 16384)
        {
            throw new InvalidDataException($"APK native library '{name}' has a load segment incompatible with 16 KB pages. Rebuild with -Wl,-z,max-page-size=16384.");
        }
        return true;
    }
}
