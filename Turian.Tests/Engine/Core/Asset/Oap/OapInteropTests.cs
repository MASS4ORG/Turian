namespace Turian.Tests;

/// <summary>
/// Golden interoperability test against <c>testdata/sample.oap</c> from the OAP reference
/// implementation. If this fails, the C# reader has diverged from the on-disk format.
/// </summary>
public class OapInteropTests
{
    static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "oap");
    static readonly string SamplePackage = Path.Combine(FixtureDir, "sample.oap");
    static readonly string SampleSources = Path.Combine(FixtureDir, "sample");

    static readonly string[] ExpectedPaths =
        ["audio/blip.wav", "main.zig", "readme.txt", "textures/hero.png"];

    /// <summary>The canonical package parses and reports four assets.</summary>
    [Fact]
    public void CanonicalPackage_ParsesFourAssets()
    {
        var reader = OapReader.OpenFile(SamplePackage);
        Assert.Equal(4, reader.Count);
        Assert.True(reader.IsSortedIndex);
    }

    /// <summary>Every declared virtual path resolves.</summary>
    [Fact]
    public void CanonicalPackage_ListsExpectedPaths()
    {
        var reader = OapReader.OpenFile(SamplePackage);
        var paths = Enumerable.Range(0, reader.Count)
            .Select(i => reader.VirtualPath(reader.EntryAt(i)))
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedPaths, paths);
    }

    /// <summary>Every asset extracts byte-for-byte identical to its source file, CRC verified.</summary>
    [Fact]
    public void CanonicalPackage_ExtractsSourcesExactly()
    {
        var reader = OapReader.OpenFile(SamplePackage);

        foreach (var relative in ExpectedPaths)
        {
            var entry = reader.FindByPath(relative);
            Assert.NotNull(entry);

            var extracted = reader.ReadAsset(entry.Value, verify: true);
            var expected = File.ReadAllBytes(Path.Combine(SampleSources, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(expected, extracted);
        }
    }

    /// <summary>The manifest names the package.</summary>
    [Fact]
    public void CanonicalPackage_ManifestNamesThePackage()
    {
        var reader = OapReader.OpenFile(SamplePackage);
        Assert.NotNull(reader.Manifest);
        var manifest = Encoding.UTF8.GetString(reader.Manifest!.Value.Span);
        Assert.Contains("\"name\":\"sample\"", manifest, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 1.1 conformance package (<c>testdata/v1.1</c>): every codec, chunking, both ciphers, alignment and a
    /// tombstone decode to the shipped source files, and ranges of the encrypted chunked asset match.
    /// </summary>
    [Fact]
    public void Version11Package_DecodesEveryFeature()
    {
        var sources = Path.Combine(FixtureDir, "v1.1", "features");
        using var reader = OapReader.OpenFile(Path.Combine(FixtureDir, "v1.1", "features.oap"));
        reader.SetKey(OapCrypto.DeriveKey("oap-1.1"));

        Assert.Equal(7, reader.Count);
        Assert.Equal(1, reader.Header.Minor);
        Assert.True((reader.Header.Flags & OapFlags.HasTombstones) != 0);
        foreach (var entry in reader.Entries.Where(static e => !e.IsTombstone))
            Assert.Equal(File.ReadAllBytes(Path.Combine(sources, reader.VirtualPath(entry))), reader.ReadAsset(entry));

        Assert.True(reader.FindByPath("removed.txt")!.Value.IsTombstone);
        Assert.Equal(0ul, reader.FindByPath("large-aligned.bin")!.Value.DataOffset % 4096);

        var encrypted = reader.FindByPath("encrypted-chunked.bin")!.Value;
        Assert.True(encrypted.IsChunked);
        var expected = File.ReadAllBytes(Path.Combine(sources, "encrypted-chunked.bin"));
        var range = new byte[300];
        Assert.Equal(300, reader.ReadAssetRange(encrypted, 4000, range));
        Assert.Equal(expected[4000..4300], range);
    }

    /// <summary>An in-memory open of the same bytes agrees with the file-backed open.</summary>
    [Fact]
    public void CanonicalPackage_InMemoryMatchesFileBacked()
    {
        var fromFile = OapReader.OpenFile(SamplePackage);
        var fromMemory = OapReader.Open(File.ReadAllBytes(SamplePackage));

        for (var i = 0; i < fromFile.Count; i++)
        {
            var a = fromFile.ReadAsset(fromFile.EntryAt(i));
            var b = fromMemory.ReadAsset(fromMemory.EntryAt(i));
            Assert.Equal(a, b);
        }
    }
}
