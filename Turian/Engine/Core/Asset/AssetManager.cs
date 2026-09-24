namespace Turian.Engine.Core;

/// <summary>
/// Coordinates opened assets, the active asset, dirty state, and per-asset selection notifications.
/// </summary>
public class AssetManager
{
    readonly HashSet<Asset> assets = [];
    readonly Dictionary<Guid, Asset> assetsById = [];
    readonly Dictionary<Guid, Node?> selectedNodesByAssetId = [];
    Asset? activeAsset;
    Node? selectedNode;

    /// <summary>
    /// Event triggered when an asset is opened or activated.
    /// </summary>
    public event Action<Asset?>? AssetOpened;

    /// <summary>
    /// Event triggered when an asset is closed.
    /// </summary>
    public event Action<Asset>? AssetClosed;

    /// <summary>
    /// Event triggered when an asset is saved.
    /// </summary>
    public event Action<Asset>? AssetSaved;

    /// <summary>
    /// Event triggered when an asset is altered.
    /// </summary>
    public event Action<Asset>? AssetAltered;

    /// <summary>
    /// Event triggered when a node is opened.
    /// </summary>
    public event Action<Node?>? NodeOpened;

    /// <summary>
    /// Event triggered when the selected node is updated in place.
    /// </summary>
    public event Action<Node?>? NodeUpdated;

    /// <summary>
    /// Event triggered when any opened node should refresh its bound views.
    /// </summary>
    public event Action<Node?>? NodeRefreshRequested;

    /// <summary>
    /// Event triggered when a non-node asset object is opened.
    /// </summary>
    public event Action<IdClass?>? IdClassOpened;

    /// <summary>
    /// Event triggered whenever the aggregate dirty state of opened assets may have changed.
    /// </summary>
    public event Action<bool>? DirtyStateChanged;

    /// <summary>
    /// Gets the currently active asset.
    /// </summary>
    public Asset? ActiveAsset => activeAsset;

    /// <summary>
    /// Gets whether any opened asset is currently dirty.
    /// </summary>
    public bool HasDirtyAssets => assets.Any(asset => asset.IsModified);

    /// <summary>
    /// Opens an asset and makes it the active asset.
    /// </summary>
    /// <param name="asset"></param>
    /// <returns></returns>
    public bool OpenAsset(Asset? asset)
    {
        if (asset is null)
        {
            ActivateAsset(null);
            return true;
        }

        var added = RegisterAsset(asset);
        ActivateAsset(asset);
        return added;
    }

    /// <summary>
    /// Activates an already-open asset, or clears the active asset when null.
    /// </summary>
    /// <param name="asset"></param>
    public void ActivateAsset(Asset? asset)
    {
        if (asset is not null)
        {
            _ = RegisterAsset(asset);
        }

        activeAsset = asset;

        if (asset is null)
        {
            selectedNode = null;
            OpenNode(null);
            OpenIdClass(null);
            AssetOpened?.Invoke(null);
            return;
        }

        selectedNode = selectedNodesByAssetId.GetValueOrDefault(asset.Id);
        AssetOpened?.Invoke(asset);

        if (selectedNode is not null)
        {
            NodeOpened?.Invoke(selectedNode);
        }
        else
        {
            OpenIdClass(asset);
        }
    }

    /// <summary>
    /// Closes an asset and updates the active asset if needed.
    /// </summary>
    /// <param name="asset"></param>
    /// <returns></returns>
    public bool CloseAsset(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (!assets.Remove(asset))
        {
            return false;
        }

        if (assetsById.TryGetValue(asset.Id, out var registeredAsset) && ReferenceEquals(registeredAsset, asset))
        {
            assetsById.Remove(asset.Id);
        }

        selectedNodesByAssetId.Remove(asset.Id);

        if (ReferenceEquals(activeAsset, asset))
        {
            activeAsset = assets.LastOrDefault();

            if (activeAsset is null)
            {
                selectedNode = null;
                OpenNode(null);
                OpenIdClass(null);
            }
            else
            {
                selectedNode = selectedNodesByAssetId.GetValueOrDefault(activeAsset.Id);
            }

            AssetOpened?.Invoke(activeAsset);

            if (activeAsset is not null)
            {
                if (selectedNode is not null)
                {
                    NodeOpened?.Invoke(selectedNode);
                }
                else
                {
                    OpenIdClass(activeAsset);
                }
            }
        }

        AssetClosed?.Invoke(asset);
        DirtyStateChanged?.Invoke(HasDirtyAssets);
        return true;
    }

    /// <summary>
    /// Saves an asset and triggers the AssetSaved event.
    /// </summary>
    /// <param name="asset"></param>
    /// <returns></returns>
    public bool SaveAsset(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var trackedAsset = GetTrackedAsset(asset);
        if (trackedAsset is null)
        {
            return false;
        }

        trackedAsset.MarkSaved();
        AssetSaved?.Invoke(trackedAsset);
        DirtyStateChanged?.Invoke(HasDirtyAssets);
        return true;
    }

    /// <summary>
    /// Saves the currently active asset, if any.
    /// </summary>
    /// <returns></returns>
    public bool SaveActiveAsset()
    {
        return activeAsset is not null && SaveAsset(activeAsset);
    }

    /// <summary>
    /// Alters an asset and triggers the AssetAltered event.
    /// </summary>
    /// <param name="asset"></param>
    /// <returns></returns>
    public bool AlterAsset(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var trackedAsset = GetTrackedAsset(asset);
        if (trackedAsset is null)
        {
            return false;
        }

        activeAsset ??= trackedAsset;
        trackedAsset.MarkModified();
        AssetAltered?.Invoke(trackedAsset);
        DirtyStateChanged?.Invoke(HasDirtyAssets);
        return true;
    }

    /// <summary>
    /// Marks the currently active asset as modified, if any.
    /// </summary>
    public void AlterAssetIfOpened()
    {
        if (activeAsset is null)
        {
            return;
        }

        _ = AlterAsset(activeAsset);
    }

    /// <summary>
    /// Marks the provided asset as modified, if it is currently opened.
    /// </summary>
    /// <param name="assetId"></param>
    public void AlterAssetIfOpened(Guid assetId)
    {
        if (assetsById.TryGetValue(assetId, out var asset))
        {
            _ = AlterAsset(asset);
        }
    }

    /// <summary>
    /// Marks the currently active asset as modified, if any, and a node is selected.
    /// </summary>
    public void AlterAssetForSelectedNode()
    {
        if (activeAsset is null || selectedNode is null)
        {
            return;
        }

        _ = AlterAsset(activeAsset);
    }

    /// <summary>
    /// Mark that a node was selected.
    /// </summary>
    /// <param name="node"></param>
    public void OpenNode(Node? node)
    {
        selectedNode = node;

        if (activeAsset is not null)
        {
            selectedNodesByAssetId[activeAsset.Id] = node;
        }

        NodeOpened?.Invoke(node);
    }

    /// <summary>
    /// Gets the node last selected for the specified asset, when available.
    /// </summary>
    /// <param name="asset"></param>
    /// <returns></returns>
    public Node? GetSelectedNodeForAsset(Asset? asset)
    {
        if (asset is null)
        {
            return null;
        }

        return selectedNodesByAssetId.GetValueOrDefault(asset.Id);
    }

    /// <summary>
    /// Notify listeners that the currently selected node changed internally
    /// without changing selection.
    /// </summary>
    public void UpdateSelectedNode()
    {
        NodeUpdated?.Invoke(selectedNode);
    }

    /// <summary>
    /// Notify listeners that the provided node changed internally
    /// without requiring selection changes.
    /// </summary>
    /// <param name="node"></param>
    public void RefreshNode(Node? node)
    {
        NodeRefreshRequested?.Invoke(node);
    }

    /// <summary>
    /// Mark that an IdClass was selected.
    /// </summary>
    /// <param name="amObject"></param>
    public void OpenIdClass(IdClass? amObject)
    {
        IdClassOpened?.Invoke(amObject);
    }

    /// <summary>
    /// Save all modified assets.
    /// </summary>
    public void SaveAllAssets()
    {
        foreach (var asset in assets.Where(asset => asset.IsModified).ToArray())
        {
            _ = SaveAsset(asset);
        }
    }

    bool RegisterAsset(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var existingAsset = assetsById.GetValueOrDefault(asset.Id);
        if (existingAsset is not null && !ReferenceEquals(existingAsset, asset))
        {
            existingAsset.RenameTo(asset.RelativePath);
            asset = existingAsset;
        }

        var added = assets.Add(asset);
        assetsById[asset.Id] = asset;
        return added;
    }

    Asset? GetTrackedAsset(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (assetsById.TryGetValue(asset.Id, out var trackedAsset))
        {
            if (!string.Equals(trackedAsset.RelativePath, asset.RelativePath, StringComparison.Ordinal))
            {
                trackedAsset.RenameTo(asset.RelativePath);
            }

            _ = assets.Add(trackedAsset);
            return trackedAsset;
        }

        if (!assets.Contains(asset))
        {
            return null;
        }

        assetsById[asset.Id] = asset;
        return asset;
    }
}
