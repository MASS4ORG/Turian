namespace Turian.Tests;

/// <summary>Covers the asset browser's open root and the inspector's Unity-style heading for what it inspects.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetLinkTests
{
    /// <summary>The Assets root starts open while the folders and the Bricks folder below it start closed.</summary>
    [Fact]
    public void AssetsRootStartsOpen()
    {
        using var browser = new AssetBrowserHarness(AssetBrowserViewMode.Tree);

        Assert.False(browser.State.IsCollapsed(browser.Root, 0));
        Assert.True(browser.State.IsCollapsed(Path.Combine(browser.Root, "Folder"), 1));
        Assert.True(browser.State.IsCollapsed(Path.Combine(browser.Project, ProjectManifest.DirectoryName), 1));
    }

    /// <summary>A collapsed root the user chose stays collapsed across rescans.</summary>
    [Fact]
    public void RescanKeepsAFoldedRoot()
    {
        using var browser = new AssetBrowserHarness(AssetBrowserViewMode.Tree);
        browser.State.SetExpanded(browser.Root, false);

        browser.Panel.Rescan(browser.Root);
        browser.Frame();

        Assert.True(browser.State.IsCollapsed(browser.Root, 0));
    }

    /// <summary>The heading names the file and its asset kind, dropping the redundant Asset suffix.</summary>
    [Fact]
    public void AssetHeadingNamesTheFileAndKind()
    {
        var inspection = new AssetInspection(new Prefab(), "/project/Assets/hero.prefab", null, "Prefab", false);

        Assert.Equal("hero.prefab (Prefab)", InspectorPanel.AssetHeading(inspection));
    }

    /// <summary>With no asset metadata the inspection's own title names the kind.</summary>
    [Fact]
    public void AssetHeadingFallsBackToTheTitle()
    {
        var inspection = new AssetInspection(null!, "/project/Settings/graphics.json", null, "GraphicsSettings", true);

        Assert.Equal("graphics.json (GraphicsSettings)", InspectorPanel.AssetHeading(inspection));
    }
}
