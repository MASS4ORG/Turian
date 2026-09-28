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
