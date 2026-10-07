namespace Turian.Tests;

/// <summary>Checks composed browser filters, scoped results, history and grid virtualization.</summary>
public sealed class AssetBrowserQueryTests
{
    readonly AssetTypeCatalog catalog = new();
    static AssetEntry Entry(string path, bool folder = false, params string[] labels) =>
        new(path, folder, folder ? null : new Asset { Labels = [.. labels] }, Path.GetDirectoryName(path));

    /// <summary>Unfiltered browsing shows only immediate children, with folders first.</summary>
    [Fact]
    public void BrowsingIsScopedAndSorted()
    {
        var entries = new[] { Entry("/Assets/z.png"), Entry("/Assets/a.png"), Entry("/Assets/Folder", true),
            Entry("/Assets/Folder/deep.png"), new AssetEntry("/Assets", true, null, null) };
        Assert.Equal(["/Assets/Folder", "/Assets/a.png", "/Assets/z.png"],
            AssetQuery.Apply(entries, "/Assets", new AssetFilter(), catalog, new HashSet<string>())
                .Select(entry => entry.AbsolutePath));
    }

    /// <summary>Name, type, every label and favorites intersect across the project.</summary>
    [Fact]
    public void FiltersComposeWithoutUnrelatedFolders()
    {
        var wanted = Entry("/Assets/Deep/Hero.png", false, "character", "red");
        var entries = new[] { wanted, Entry("/Assets/Other/Hero.png", false, "character"),
            Entry("/Assets/Hero.obj", false, "character", "red"), Entry("/Assets/HeroFolder", true) };
        var filter = new AssetFilter
        {
            Name = "HERO",
            Types = new HashSet<string> { "turian.texture" },
            Labels = new HashSet<string> { "character", "red" },
            FavoritesOnly = true
        };
        Assert.Equal([wanted], AssetQuery.Apply(entries, "/Assets", filter, catalog,
            entries.Select(entry => entry.AbsolutePath).ToHashSet()));
        Assert.Empty(AssetQuery.Apply(entries, "/Assets", filter, catalog, new HashSet<string>()));
        Assert.True(AssetQuery.Matches(entries[3], filter, catalog, new HashSet<string>(), keepFolders: true));
    }

    /// <summary>Unknown files and directories have explicit filter kinds; roots do not appear in search.</summary>
    [Fact]
    public void FolderUnknownAndLabelFiltersArePredictable()
    {
        var folder = Entry("/Assets/Folder", true);
        var other = Entry("/Assets/file.unknown");
        var entries = new[] { folder, other, new AssetEntry("/Assets", true, null, null),
            new AssetEntry("/Assets/plain.cs", false, null, "/Assets") };
        Assert.Equal([folder], AssetQuery.Apply(entries, "/Assets", new AssetFilter
        { Types = new HashSet<string> { AssetQuery.FolderType } }, catalog, new HashSet<string>()));
        Assert.Equal([other], AssetQuery.Apply(entries, "/Assets", new AssetFilter
        { Types = new HashSet<string> { AssetQuery.OtherType } }, catalog, new HashSet<string>()));
        Assert.Empty(AssetQuery.Apply(entries, "/Assets", new AssetFilter
        { Labels = new HashSet<string> { "missing" } }, catalog, new HashSet<string>()));
        Assert.Equal(3, AssetQuery.Apply(entries, "/elsewhere", new AssetFilter { Name = "l" }, catalog,
            new HashSet<string>()).Count);
    }

    /// <summary>Navigation truncates forward history, caps visits and remaps only matching descendants.</summary>
    [Fact]
    public void HistoryIsBoundedAndRemapped()
    {
        var navigation = new AssetBrowserNavigation();
        Assert.Null(navigation.Current);
        navigation.Back();
        navigation.Forward();
        navigation.Reset("/Assets");
        navigation.Visit("/Assets");
        Assert.False(navigation.CanBack);
        navigation.Visit("/Assets/Folder");
        navigation.Visit("/Assets/Folder/Sub");
        navigation.Back();
        Assert.True(navigation.CanForward);
        navigation.Forward();
        Assert.Equal("/Assets/Folder/Sub", navigation.Current);
        navigation.Remap("/Assets/Folder", "/Assets/Moved");
        Assert.Equal("/Assets/Moved/Sub", navigation.Current);
        navigation.Back();
        navigation.Visit("/Assets/Other");
        Assert.False(navigation.CanForward);
        Assert.Equal("/Assets/Folder2", AssetPathSegments.Remap("/Assets/Folder2", "/Assets/Folder", "/else"));
        for (var i = 0; i < 40; i++) navigation.Visit($"/Assets/{i}");
        var backCount = 0;
        while (navigation.CanBack) { navigation.Back(); backCount++; }
        Assert.Equal(31, backCount);
    }

    /// <summary>Breadcrumb trails follow scanned parents, including brick roots.</summary>
    [Fact]
    public void BreadcrumbsFollowTreeRoots()
    {
        var entries = new[] { new AssetEntry("/Brick", true, null, null),
            new AssetEntry("/Brick/Textures", true, null, "/Brick") };
        Assert.Equal(["Brick", "Textures"], AssetPathSegments.Build("/Brick/Textures", entries).Select(segment => segment.Name));
        Assert.Empty(AssetPathSegments.Build("/unknown", entries));
    }

    /// <summary>A large folder builds a bounded set of visible bands at any scroll position.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(20000)]
    public void LargeGridIsVirtualized(float scroll)
    {
        var window = AssetGridLayout.Calculate(5000, 500, 100, 120, scroll, 300);
        Assert.Equal(5, window.Columns);
        Assert.Equal(1000, window.RowCount);
        Assert.InRange(window.LastRow - window.FirstRow, 1, 5);
        Assert.Equal(0, AssetGridLayout.Calculate(0, 0, 0, 0, 0, 0).RowCount);
    }
}
