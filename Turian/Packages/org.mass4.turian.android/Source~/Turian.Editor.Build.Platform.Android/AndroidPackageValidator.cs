using System.Buffers.Binary;
using System.Text;

namespace Turian.Editor.Core;

/// <summary>Checks that an APK contains the runtime content and stores OAP entries without ZIP compression.</summary>
public static class AndroidPackageValidator
{
    /// <summary>Rejects missing runtime content, ZIP64 archives and compressed OAP entries.</summary>
    /// <param name="path">The APK to inspect.</param>
    public static void Validate(string path)
    {
        using var stream = File.OpenRead(path);
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
        stream.Position = offset;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var storedPackages = 0;
        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "assets/projectSettings.json", "assets/usercode.typeids.json", "assets/Content/assetCatalog.json",
        };
        for (var i = 0; i < count; i++)
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
            stream.Seek(extraLength + commentLength, SeekOrigin.Current);
            required.Remove(name);
            if (!name.StartsWith("assets/", StringComparison.Ordinal)
                || !name.EndsWith(".oap", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (method != 0)
            {
                throw new InvalidDataException($"APK entry '{name}' is compressed. Add .oap to AndroidStoreUncompressedFileExtensions.");
            }
            storedPackages++;
        }
        if (storedPackages == 0 || required.Count != 0)
        {
            throw new InvalidDataException("APK runtime content is incomplete: OAP packages, settings, type manifest and runtime catalog are required.");
        }
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
