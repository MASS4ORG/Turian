namespace Turian.Editor.Core;

/// <summary>
/// Queries asset database for candidate assets based on type compatibility.
/// </summary>
public static class AssetReferenceQuery
{
    /// <summary>
    /// Gets candidate assets that match the specified asset type.
    /// </summary>
    public static IReadOnlyList<AssetCandidateItem> GetCandidates(
        AssetDatabase assetDatabase,
        Type assetType,
        string? search = null)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(assetType);
        var snapshot = assetDatabase.GetAssetsSnapshot();

        return snapshot
            .Where(r => IsAssetTypeMatch(r.AssetTypeName, assetType))
            .Where(r => MatchesSearch(r, search))
            .Select(r => new AssetCandidateItem(r.AssetId, GetDisplayName(r.SourceRelativePath), r.AssetTypeName))
            .ToList();
    }

    /// <summary>
    /// Gets candidate prefabs that contain the specified component type.
    /// </summary>
    public static IReadOnlyList<AssetCandidateItem> GetPrefabCandidates(
        AssetDatabase assetDatabase,
        Type componentType,
        string? search = null)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(componentType);
        var snapshot = assetDatabase.GetAssetsSnapshot();

        return snapshot
            .Where(r => IsPrefabRecord(r.AssetTypeName))
            .Where(r => HasCompatibleComponent(r.ComponentTypes, componentType))
            .Where(r => MatchesSearch(r, search))
            .Select(r => new AssetCandidateItem(r.AssetId, GetDisplayName(r.SourceRelativePath), r.AssetTypeName))
            .ToList();
    }

    /// <summary>
    /// Checks if the asset record is valid for the specified asset type.
    /// </summary>
    public static bool IsValidForAssetType(AssetRecord record, Type assetType)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(assetType);
        var resolved = TryResolveType(record.AssetTypeName);
        return resolved is not null && assetType.IsAssignableFrom(resolved);
    }

    /// <summary>
    /// Checks if the asset record is a prefab containing the specified component type.
    /// </summary>
    public static bool IsValidForPrefabComponentType(AssetRecord record, Type componentType)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(componentType);
        return IsPrefabRecord(record.AssetTypeName)
            && HasCompatibleComponent(record.ComponentTypes, componentType);
    }

    /// <summary>
    /// Builds an asset instance from a catalog record, for callers that only have an id — reopening a
    /// document, resolving a command-line reference. Returns null when the record names a type this
    /// build cannot load.
    /// </summary>
    /// <param name="record">The catalog record to materialise.</param>
    public static Asset? CreateAsset(AssetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (TryResolveType(record.AssetTypeName) is not { } type) return null;
        if (Activator.CreateInstance(type) is not Asset asset) return null;

        asset.Id = record.AssetId;
        asset.RelativePath = record.SourceRelativePath;
        return asset;
    }

    static bool IsAssetTypeMatch(string recordTypeName, Type assetType)
    {
        if (string.IsNullOrWhiteSpace(recordTypeName)) return false;
        if (string.Equals(recordTypeName, assetType.FullName, StringComparison.Ordinal)) return true;
        if (string.Equals(recordTypeName, assetType.Name, StringComparison.Ordinal)) return true;

        var resolved = TryResolveType(recordTypeName);
        return resolved is not null && assetType.IsAssignableFrom(resolved);
    }

    static bool IsPrefabRecord(string assetTypeName)
    {
        if (string.IsNullOrWhiteSpace(assetTypeName)) return false;

        var resolved = TryResolveType(assetTypeName);
        if (resolved is not null) return typeof(Prefab).IsAssignableFrom(resolved);

        return assetTypeName.EndsWith(nameof(Prefab), StringComparison.Ordinal);
    }

    static bool HasCompatibleComponent(List<string> componentTypes, Type componentType)
    {
        if (componentTypes is not { Count: > 0 }) return false;

        return componentTypes.Any(name =>
        {
            var t = TryResolveType(name);
            return t is not null && componentType.IsAssignableFrom(t);
        });
    }

    static bool MatchesSearch(AssetRecord record, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return GetDisplayName(record.SourceRelativePath).Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    static string GetDisplayName(string sourcePath) => Path.GetFileNameWithoutExtension(sourcePath);

    static Type? TryResolveType(string typeName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a =>
            {
                try { return a.GetType(typeName); }
                catch { return null; }
            })
            .FirstOrDefault(t => t is not null);
}
