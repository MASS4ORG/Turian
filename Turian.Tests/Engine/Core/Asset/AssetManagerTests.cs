namespace Turian.Tests;

/// <summary>
/// Tests for the AssetManager class.
/// </summary>
public class AssetManagerTests
{
    /// <summary>
    /// Verifies that opening an asset correctly sets it as the active asset.
    /// </summary>
    [Fact]
    public void OpenAsset_SetsActiveAsset()
    {
        var manager = new AssetManager();
        var asset = new Asset { RelativePath = "test.asset" };

        manager.OpenAsset(asset);

        Assert.Equal(asset, manager.ActiveAsset);
    }

    /// <summary>
    /// Verifies that closing an asset correctly clears the active asset if it was the closed one.
    /// </summary>
    [Fact]
    public void CloseAsset_ClearsActiveAsset()
    {
        var manager = new AssetManager();
        var asset = new Asset { RelativePath = "test.asset" };

        manager.OpenAsset(asset);
        manager.CloseAsset(asset);

        Assert.Null(manager.ActiveAsset);
    }

    /// <summary>
    /// Verifies that modifying an asset updates the dirty state correctly.
    /// </summary>
    [Fact]
    public void AssetModified_UpdatesDirtyState()
    {
        var manager = new AssetManager();
        var asset = new Asset { RelativePath = "test.asset" };

        manager.OpenAsset(asset);
        Assert.False(manager.HasDirtyAssets);

        manager.AlterAsset(asset);
        Assert.True(manager.HasDirtyAssets);

        manager.SaveAsset(asset);
        Assert.False(manager.HasDirtyAssets);
    }

    /// <summary>
    /// Verifies that AssetManager events are triggered in response to asset operations.
    /// </summary>
    [Fact]
    public void Events_TriggerCorrectly()
    {
        var manager = new AssetManager();
        var asset = new Asset { RelativePath = "test.asset" };
        var openedCount = 0;
        var closedCount = 0;
        var alteredCount = 0;
        var savedCount = 0;

        manager.AssetOpened += _ => openedCount++;
        manager.AssetClosed += _ => closedCount++;
        manager.AssetAltered += _ => alteredCount++;
        manager.AssetSaved += _ => savedCount++;

        manager.OpenAsset(asset);
        Assert.Equal(1, openedCount);

        manager.AlterAsset(asset);
        Assert.Equal(1, alteredCount);

        manager.SaveAsset(asset);
        Assert.Equal(1, savedCount);

        manager.CloseAsset(asset);
        Assert.Equal(1, closedCount);
    }
}
