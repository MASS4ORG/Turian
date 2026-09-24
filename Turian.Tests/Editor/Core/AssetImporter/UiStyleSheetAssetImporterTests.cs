namespace Turian.Tests;

/// <summary>Tests for <see cref="UiStyleSheetAssetImporter"/> — extension match, validation, artifact copy.</summary>
public sealed class UiStyleSheetAssetImporterTests
{
    readonly UiStyleSheetAssetImporter importer = new();

    /// <summary>Only <c>.uss</c> files (any case) are accepted.</summary>
    [Theory]
    [InlineData("theme.uss", true)]
    [InlineData("theme.USS", true)]
    [InlineData("menu.ui", false)]
    [InlineData("", false)]
    public void IsValid_MatchesUssExtension(string path, bool expected) =>
        Assert.Equal(expected, importer.IsValid(path));

    /// <summary>Creating an asset from a path yields a <see cref="UiStyleSheetAsset"/> with that relative path.</summary>
    [Fact]
    public void CreateAsset_ReturnsUiStyleSheetAsset()
    {
        var asset = importer.CreateAsset("Assets/UI/theme.uss");
        var sheet = Assert.IsType<UiStyleSheetAsset>(asset);
        Assert.Equal("Assets/UI/theme.uss", sheet.RelativePath);
    }

    /// <summary>Importing copies a valid sheet to the cache unchanged.</summary>
    [Fact]
    public void ImportToCache_CopiesValidSheet()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uss-import-{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        Directory.CreateDirectory(importDir);
        try
        {
            var source = Path.Combine(root, "theme.uss");
            File.WriteAllText(source, ".btn { background-color: #39435a; } .btn:hover { background-color: #4a90e2; }");

            var artifacts = importer.ImportToCache(importer.CreateAsset("theme.uss"), source, importDir);

            var copied = Path.Combine(importDir, Assert.Single(artifacts));
            Assert.True(File.Exists(copied));
            Assert.Contains(".btn", File.ReadAllText(copied), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A malformed sheet is still copied through instead of failing the import.</summary>
    [Fact]
    public void ImportToCache_MalformedSheet_CopiesSourceWithoutThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uss-bad-{Guid.NewGuid():N}");
        var importDir = Path.Combine(root, "import");
        Directory.CreateDirectory(importDir);
        try
        {
            var source = Path.Combine(root, "bad.uss");
            File.WriteAllText(source, ".btn { color: #fff; "); // unterminated block

            var artifacts = importer.ImportToCache(importer.CreateAsset("bad.uss"), source, importDir);

            Assert.True(File.Exists(Path.Combine(importDir, Assert.Single(artifacts))));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
