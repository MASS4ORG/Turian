using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Turian.Editor.Core;

/// <summary>Checks APK runtime content and native-library page alignment.</summary>
public static class AndroidPackageValidator
{
    /// <summary>The Vulkan validation layer library, which only development builds may ship.</summary>
    public const string ValidationLayerFile = "libVkLayer_khronos_validation.so";

    /// <summary>Rejects incomplete content, compressed OAPs, misplaced debug layers and libraries incompatible with 16 KB pages.</summary>
    /// <param name="path">The APK to inspect.</param>
    /// <param name="development">Whether the APK is a development build, the only kind allowed the validation layer.</param>
    public static void Validate(string path, bool development = false)
    {
        using var stream = File.OpenRead(path);
        ValidateContentDirectory(stream, development);
        ValidateNativeLibraries(stream);
    }

    static (ushort Count, uint Offset) ReadDirectoryLocation(Stream stream)
    {
        var tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        var end = FindEnd(tail);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (count == ushort.MaxValue || offset == uint.MaxValue)
        {
            throw new InvalidDataException("ZIP64 APKs are not supported by the Android content validator.");
        }
        return (count, offset);
    }

    static void ValidateContentDirectory(Stream stream, bool development)
    {
        var (count, offset) = ReadDirectoryLocation(stream);
        stream.Position = offset;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var storedPackages = 0;
        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "assets/projectSettings.json", "assets/usercode.typeids.json", "assets/Content/assetCatalog.json",
        };
        for (var i = 0; i < count; i++)
        {
            var entry = ReadEntry(reader);
            if (entry.Method == 0 && IsNativeLibrary(entry.Name))
            {
                ValidateNativeZipAlignment(stream, entry.Header, entry.Name);
            }
            required.Remove(entry.Name);
            if (!development && entry.Name.EndsWith("/" + ValidationLayerFile, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"APK entry '{entry.Name}' is a Vulkan validation layer, which only development builds may ship.");
            }
            if (ValidateContentEntry(entry))
            {
                storedPackages++;
            }
        }
        if (storedPackages == 0 || required.Count != 0)
        {
            throw new InvalidDataException("APK runtime content is incomplete: OAP packages, settings, type manifest and runtime catalog are required.");
        }
    }

    static CentralEntry ReadEntry(BinaryReader reader)
    {
        var header = reader.ReadBytes(46);
        if (header.Length != 46 || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
        {
            throw new InvalidDataException("Invalid APK central directory.");
        }
        var method = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
        var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
        reader.BaseStream.Seek(extraLength + commentLength, SeekOrigin.Current);
        return new(header, name, method);
    }

    static bool ValidateContentEntry(CentralEntry entry)
    {
        if (!entry.Name.StartsWith("assets/", StringComparison.Ordinal)
            || !entry.Name.EndsWith(".oap", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (entry.Method != 0)
        {
            throw new InvalidDataException($"APK entry '{entry.Name}' is compressed. Add .oap to AndroidStoreUncompressedFileExtensions.");
        }
        return true;
    }

    static bool IsNativeLibrary(string name) => name.StartsWith("lib/", StringComparison.Ordinal)
        && name.EndsWith(".so", StringComparison.Ordinal);

    static void ValidateNativeLibraries(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries.Where(static entry => IsNativeLibrary(entry.FullName)))
        {
            using var source = entry.Open();
            using var image = new MemoryStream();
            source.CopyTo(image);
            image.Position = 0;
            AndroidNativeLibraryValidator.Validate(image, entry.FullName);
        }
    }

    readonly record struct CentralEntry(byte[] Header, string Name, ushort Method);

    static void ValidateNativeZipAlignment(Stream stream, byte[] centralHeader, string name)
    {
        var position = stream.Position;
        stream.Position = BinaryPrimitives.ReadUInt32LittleEndian(centralHeader.AsSpan(42));
        Span<byte> localHeader = stackalloc byte[30];
        stream.ReadExactly(localHeader);
        if (BinaryPrimitives.ReadUInt32LittleEndian(localHeader) != 0x04034b50)
        {
            throw new InvalidDataException($"APK entry '{name}' has an invalid local ZIP header.");
        }
        var dataOffset = stream.Position + BinaryPrimitives.ReadUInt16LittleEndian(localHeader[26..])
            + BinaryPrimitives.ReadUInt16LittleEndian(localHeader[28..]);
        if (dataOffset % 16384 != 0)
        {
            throw new InvalidDataException($"APK native entry '{name}' is not ZIP-aligned to 16 KB. Run zipalign -P 16.");
        }
        stream.Position = position;
    }

    static int FindEnd(byte[] tail)
    {
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50
                && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length)
            {
                return i;
            }
        }
        throw new InvalidDataException("APK has no ZIP end-of-directory record.");
    }
}
