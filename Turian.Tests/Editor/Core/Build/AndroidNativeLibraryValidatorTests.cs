namespace Turian.Tests;

/// <summary>Native APK validation covers ELF load-segment alignment independently of ZIP storage.</summary>
public sealed class AndroidNativeLibraryValidatorTests
{
    /// <summary>Stored native libraries pass when both their ZIP data and ELF load segments are 16 KB aligned.</summary>
    [Fact]
    public void AcceptsAlignedStoredNativeLibrary()
    {
        const string nativeName = "lib/arm64-v8a/example.so";
        var path = Path.Combine(Path.GetTempPath(), "turian-native-" + Guid.NewGuid().ToString("N") + ".apk");
        try
        {
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var name in new[]
                {
                    "projectSettings.json", "usercode.typeids.json", "Content/assetCatalog.json", "Content/example.oap",
                })
                {
                    using var entry = archive.CreateEntry("assets/" + name, CompressionLevel.NoCompression).Open();
                    entry.Write([0]);
                }
                var padding = (int)((16384 - (file.Position + 33 + 30 + nativeName.Length) % 16384) % 16384);
                using (var entry = archive.CreateEntry("pad", CompressionLevel.NoCompression).Open())
                {
                    entry.Write(new byte[padding]);
                }
                using var native = archive.CreateEntry(nativeName, CompressionLevel.NoCompression).Open();
                using var image = CreateImage(16384, 0, 0);
                image.CopyTo(native);
            }
            AndroidPackageValidator.Validate(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>APK validation checks ELF segments independently of stored native ZIP-entry alignment.</summary>
    [Theory]
    [InlineData(4096, false, false)]
    [InlineData(16384, true, false)]
    [InlineData(16384, false, true)]
    public void ChecksApkNativeLibrary(ulong alignment, bool valid, bool stored)
    {
        var path = Path.Combine(Path.GetTempPath(), "turian-native-" + Guid.NewGuid().ToString("N") + ".apk");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var name in new[]
                {
                    "projectSettings.json", "usercode.typeids.json", "Content/assetCatalog.json", "Content/example.oap",
                })
                {
                    using var entry = archive.CreateEntry("assets/" + name, CompressionLevel.NoCompression).Open();
                    entry.Write([0]);
                }
                using var native = archive.CreateEntry("lib/arm64-v8a/example.so",
                    stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                using var image = CreateImage(alignment, 0, 0);
                image.CopyTo(native);
            }
            if (valid)
            {
                AndroidPackageValidator.Validate(path);
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => AndroidPackageValidator.Validate(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Both segment alignment and file/virtual address congruence are required.</summary>
    [Theory]
    [InlineData(16384, 0, 0, true)]
    [InlineData(65536, 4096, 20480, true)]
    [InlineData(4096, 0, 0, false)]
    [InlineData(16384, 4096, 8192, false)]
    [InlineData(24576, 0, 0, false)]
    public void ChecksLoadSegment(ulong alignment, ulong offset, ulong address, bool valid)
    {
        using var image = CreateImage(alignment, offset, address);
        if (valid)
        {
            AndroidNativeLibraryValidator.Validate(image, "lib/example.so");
        }
        else
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                AndroidNativeLibraryValidator.Validate(image, "lib/example.so"));
            Assert.Contains("lib/example.so", error.Message);
            Assert.Contains("max-page-size=16384", error.Message);
        }
    }

    /// <summary>Malformed native payloads cannot bypass segment checks.</summary>
    [Theory]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(54, 0)]
    [InlineData(56, 0)]
    [InlineData(32, 255)]
    [InlineData(64, 0)]
    public void RejectsInvalidImages(int position, byte value)
    {
        using var image = CreateImage(16384, 0, 0);
        image.GetBuffer()[position] = value;
        Assert.Throws<InvalidDataException>(() => AndroidNativeLibraryValidator.Validate(image, "invalid.so"));
    }

    static MemoryStream CreateImage(ulong alignment, ulong offset, ulong address)
    {
        var image = new MemoryStream(120);
        image.SetLength(120);
        var bytes = image.GetBuffer().AsSpan();
        new byte[] { 0x7f, 0x45, 0x4c, 0x46, 2, 1 }.CopyTo(bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[54..], 56);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[56..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[64..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[72..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[80..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[112..], alignment);
        return image;
    }
}
