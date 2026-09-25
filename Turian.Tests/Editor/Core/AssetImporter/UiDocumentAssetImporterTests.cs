namespace Turian.Tests;

/// <summary>Tests for <see cref="UiDocumentAssetImporter"/> — validation, JSON baking, dependency recording.</summary>
public sealed class UiDocumentAssetImporterTests
{
    readonly UiDocumentAssetImporter importer = new();

    /// <summary>Only <c>.ui</c> files (any case) are accepted.</summary>
    [Theory]
    [InlineData("menu.ui", true)]
    [InlineData("menu.UI", true)]
    [InlineData("theme.uss", false)]
    [InlineData("button.png", false)]
    [InlineData("", false)]
    public void IsValid_MatchesUiExtension(string path, bool expected) =>
        Assert.Equal(expected, importer.IsValid(path));

    /// <summary>Creating an asset from a path yields a <see cref="UiDocumentAsset"/> with that relative path.</summary>
    [Fact]
    public void CreateAsset_ReturnsUiDocumentAssetWithPath()
    {
        var asset = importer.CreateAsset("Assets/UI/menu.ui");
        var doc = Assert.IsType<UiDocumentAsset>(asset);
        Assert.Equal("Assets/UI/menu.ui", doc.RelativePath);
    }

    /// <summary>Importing bakes a JSON <c>.amui</c> artifact that round-trips to the same document.</summary>
    [Fact]
    public void ImportToCache_WritesRoundTrippableJson()
    {
        using var dir = new TempDir();
        var source = dir.Write("menu.ui",
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <VisualElement name="root"><Label text="Hi" /></VisualElement>
            </UI>
            """);
        var importDir = dir.Sub("import");

        var artifacts = importer.ImportToCache(importer.CreateAsset("menu.ui"), source, importDir);

        var baked = Path.Combine(importDir, Assert.Single(artifacts));
        Assert.EndsWith(".amui", baked, StringComparison.Ordinal);
        var document = UiDocument.FromJson(File.ReadAllText(baked));
        Assert.Equal("root", document.Root.Name);
    }

    /// <summary>Malformed XML bakes an empty document instead of throwing.</summary>
    [Fact]
    public void ImportToCache_MalformedXml_BakesEmptyDocumentWithoutThrowing()
    {
        using var dir = new TempDir();
        var source = dir.Write("bad.ui", "<UI><VisualElement></UI>");
        var importDir = dir.Sub("import");

        var artifacts = importer.ImportToCache(importer.CreateAsset("bad.ui"), source, importDir);

        var baked = Path.Combine(importDir, Assert.Single(artifacts));
        var document = UiDocument.FromJson(File.ReadAllText(baked));
        Assert.Empty(document.Root.Children);
    }

    /// <summary>Referenced files that exist are recorded as dependencies; missing ones are skipped.</summary>
    [Fact]
    public void CreateChildAssets_RecordsReferencedFilesThatExist()
    {
        using var dir = new TempDir();
        dir.Write("theme.uss", ".x { color: #fff; }");
        dir.Write("button.png", "not really a png");
        var source = dir.Write("menu.ui",
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <Style src="theme.uss" />
              <ImageButton image-normal="button.png" image-hover="missing.png" />
            </UI>
            """);

        var context = new RecordingContext();
        _ = importer.CreateChildAssets(Guid.NewGuid(), source, context).ToList();

        Assert.Contains(Path.Combine(dir.Path, "theme.uss"), context.Ensured);
        Assert.Contains(Path.Combine(dir.Path, "button.png"), context.Ensured);
        Assert.DoesNotContain(Path.Combine(dir.Path, "missing.png"), context.Ensured);
    }

    sealed class RecordingContext : IAssetImportContext
    {
        public List<string> Ensured { get; } = [];

        public Guid EnsureAsset(string absolutePath)
        {
            Ensured.Add(Path.GetFullPath(absolutePath));
            return Guid.NewGuid();
        }

        public void ConfigureTexture(string absolutePath, bool isSrgb, bool flipGreenChannel) { }
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ui-import-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public string Write(string name, string content)
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllText(full, content);
            return full;
        }

        public string Sub(string name)
        {
            var full = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(full);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
