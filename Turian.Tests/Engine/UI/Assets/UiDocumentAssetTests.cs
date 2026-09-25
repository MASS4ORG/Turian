namespace Turian.Tests;

/// <summary>Tests for <see cref="UiDocumentAsset"/> / <see cref="UiStyleSheetAsset"/> content parsing.</summary>
public sealed class UiDocumentAssetTests
{
    /// <summary>XML source text parses into a document.</summary>
    [Fact]
    public void ParseText_ReadsXml()
    {
        var doc = UiDocumentAsset.ParseText(
            """
            <UI xmlns="https://turian.mass4.org/ui"><VisualElement name="root" /></UI>
            """);

        Assert.Equal("root", doc.Root.Name);
    }

    /// <summary>Baked JSON parses back into the same document structure.</summary>
    [Fact]
    public void ParseText_ReadsBakedJson()
    {
        var original = UiDocumentAsset.ParseText(
            """
            <UI xmlns="https://turian.mass4.org/ui"><VisualElement name="root"><Label text="Hi" /></VisualElement></UI>
            """);

        var reparsed = UiDocumentAsset.ParseText(original.ToJson());

        Assert.Equal("root", reparsed.Root.Name);
        Assert.Single(reparsed.Root.Children);
    }

    /// <summary>A <c>.ui</c> file on disk loads with its style references.</summary>
    [Fact]
    public void LoadContent_ReadsUiFileFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"doc-{Guid.NewGuid():N}.ui");
        File.WriteAllText(path,
            """
            <UI xmlns="https://turian.mass4.org/ui">
              <Style src="theme.uss" />
              <VisualElement name="root" />
            </UI>
            """);
        try
        {
            var doc = UiDocumentAsset.LoadContent(path);
            Assert.Contains("theme.uss", doc.StyleSheets);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A <c>.uss</c> file on disk loads into parsed rules.</summary>
    [Fact]
    public void StyleSheet_LoadContent_ParsesFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sheet-{Guid.NewGuid():N}.uss");
        File.WriteAllText(path, "--accent: #4a90e2;\n.btn { background-color: var(--accent); }");
        try
        {
            var sheet = UiStyleSheetAsset.LoadContent(path);
            Assert.Single(sheet.Rules);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
