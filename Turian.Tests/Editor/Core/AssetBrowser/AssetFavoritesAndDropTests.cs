namespace Turian.Tests;

/// <summary>Checks project-specific favorites and common drag payload policies.</summary>
public sealed class AssetFavoritesAndDropTests
{
    /// <summary>Favorites reload privately, prune missing paths, remap descendants and tolerate damaged JSON.</summary>
    [Fact]
    public void FavoritesArePrivateResilientAndFollowFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-favorites-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "Project");
        var config = Path.Combine(root, "Config");
        var folder = Path.Combine(project, "Assets", "Folder");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "a.txt");
        File.WriteAllText(path, "a");
        try
        {
            var favorites = new AssetFavoritesStore(project, config);
            favorites.Set(path, true);
            favorites.Set(folder, true);
            Assert.Equal(2, new AssetFavoritesStore(project + Path.DirectorySeparatorChar, config).Paths.Count);
            Assert.Empty(new AssetFavoritesStore(Path.Combine(root, "Other"), config).Paths);
            var moved = Path.Combine(project, "Assets", "Moved");
            Directory.Move(folder, moved);
            favorites.Remap(folder, moved);
            Assert.Contains(Path.Combine(moved, "a.txt"), new AssetFavoritesStore(project, config).Paths);
            favorites.Set(moved, false);
            Assert.Single(favorites.Paths);
            File.Delete(Path.Combine(moved, "a.txt"));
            Assert.Empty(new AssetFavoritesStore(project, config).Paths);
            var file = Directory.GetFiles(Path.Combine(config, "favorites"), "*.json").Single();
            File.WriteAllText(file, "broken");
            Assert.Empty(new AssetFavoritesStore(project, config).Paths);
            File.WriteAllText(file, "null");
            Assert.Empty(new AssetFavoritesStore(project, config).Paths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Invalid favorites storage does not interrupt browser operations.</summary>
    [Fact]
    public void UnwritableFavoritesStorageKeepsSessionSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-favorites-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "not-a-directory");
        File.WriteAllText(config, "file");
        try
        {
            var store = new AssetFavoritesStore(root, config);
            store.Set("/asset", true);
            Assert.Contains("/asset", store.Paths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Project assets move, brick assets copy, and mixed or cyclic transfers are rejected.</summary>
    [Fact]
    public void DropPolicyProtectsRootsAndReadOnlyAssets()
    {
        var target = new AssetEntry("/Assets/Folder", true, null, "/Assets");
        var source = new AssetEntry("/Assets/a.txt", false, new Asset(), "/Assets");
        var brick = source with { IsReadOnly = true };
        ReferenceDragPayload Payload(params AssetEntry[] entries) => new(Guid.Empty, "a", source.AbsolutePath)
        { Entries = entries };
        Assert.False(AssetDropPolicy.Resolve(target, Payload(source), [source])!.Copy);
        Assert.True(AssetDropPolicy.Resolve(target, Payload(brick), [brick])!.Copy);
        Assert.Null(AssetDropPolicy.Resolve(target, Payload(source, brick), [source, brick]));
        Assert.Null(AssetDropPolicy.Resolve(target with { IsReadOnly = true }, Payload(source), [source]));
        Assert.Null(AssetDropPolicy.Resolve(target with { IsDirectory = false }, Payload(source), [source]));
        Assert.Null(AssetDropPolicy.Resolve(target, Payload(new AssetEntry("/Assets", true, null, null)), [source]));
        Assert.Null(AssetDropPolicy.Resolve(target, Payload(target), [source]));
        Assert.Null(AssetDropPolicy.Resolve(target, new object(), [source]));
        Assert.NotNull(AssetDropPolicy.Resolve(target, new ReferenceDragPayload(Guid.Empty, "a", source.AbsolutePath), [source]));
        Assert.NotNull(AssetDropPolicy.Resolve(target, new ScriptDragPayload(typeof(DraggedComponent), "a", source.AbsolutePath), [source]));
    }

    /// <summary>Scripts carry component payloads, while unknown scripts and multi-selection remain reference drags.</summary>
    [Fact]
    public void PayloadsRemainCompatibleWithReferenceAndComponentTargets()
    {
        var script = new AssetEntry("/Assets/DraggedComponent.cs", false, null, "/Assets");
        var payload = AssetDragPayloads.Create(script, [], [typeof(DraggedComponent).Assembly]);
        Assert.IsType<ScriptDragPayload>(payload);
        Assert.IsType<ReferenceDragPayload>(AssetDragPayloads.Create(script, [], []));
        var second = script with { AbsolutePath = "/Assets/second.cs" };
        Assert.Equal(2, Assert.IsType<ReferenceDragPayload>(AssetDragPayloads.Create(script, [script, second], [])).Entries.Count);
        Assert.Single(Assert.IsType<ReferenceDragPayload>(AssetDragPayloads.Create(script, [second], [])).Entries);
    }

    /// <summary>A component type a compiled script can drag into the scene tree.</summary>
    public sealed class DraggedComponent : Component;
}
