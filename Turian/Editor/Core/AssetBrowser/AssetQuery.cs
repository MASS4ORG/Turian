namespace Turian.Editor.Core;

/// <summary>Composes browser filters independently of the view displaying their results.</summary>
public sealed record AssetFilter
{
    /// <summary>A case-insensitive name substring.</summary>
    public string Name { get; init; } = "";

    /// <summary>Allowed kind ids, including folder and other; an empty set allows every kind.</summary>
    public IReadOnlySet<string> Types { get; init; } = new HashSet<string>();

    /// <summary>Labels an asset must all carry.</summary>
    public IReadOnlySet<string> Labels { get; init; } = new HashSet<string>();

    /// <summary>Restricts results to favorite paths.</summary>
    public bool FavoritesOnly { get; init; }
}

/// <summary>Queries assets with stable folder-first ordering.</summary>
public static class AssetQuery
{
    /// <summary>The kind used for directories.</summary>
    public const string FolderType = "folder";

    /// <summary>The kind used for unregistered extensions.</summary>
    public const string OtherType = "other";

    /// <summary>Returns current-folder contents, or project-wide results while any filter is active.</summary>
    public static IReadOnlyList<AssetEntry> Apply(IEnumerable<AssetEntry> entries, string folder,
        AssetFilter filter, AssetTypeCatalog catalog, IReadOnlySet<string> favorites)
    {
        var global = IsActive(filter);
        return [.. entries.Where(entry => global ? entry.ParentPath is not null : entry.ParentPath == folder)
            .Where(entry => Matches(entry, filter, catalog, favorites, keepFolders: !global))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => Path.GetFileName(entry.AbsolutePath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.AbsolutePath, StringComparer.Ordinal)];
    }

    /// <summary>Whether results need to span the project rather than just the current folder.</summary>
    public static bool IsActive(AssetFilter filter) => filter.Name.Length > 0 || filter.Types.Count > 0
        || filter.Labels.Count > 0 || filter.FavoritesOnly;

    /// <summary>Tests all active filters; browsing folders remain navigable outside a results view.</summary>
    public static bool Matches(AssetEntry entry, AssetFilter filter, AssetTypeCatalog catalog,
        IReadOnlySet<string> favorites, bool keepFolders = false)
    {
        if (keepFolders && entry.IsDirectory) return true;
        if (!Path.GetFileName(entry.AbsolutePath).Contains(filter.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.FavoritesOnly && !favorites.Contains(entry.AbsolutePath)) return false;
        return MatchesType(entry, filter, catalog) && MatchesLabels(entry, filter.Labels);
    }

    static bool MatchesType(AssetEntry entry, AssetFilter filter, AssetTypeCatalog catalog)
    {
        var kind = entry.IsDirectory ? FolderType : catalog.Resolve(entry.AbsolutePath)?.Id ?? OtherType;
        return filter.Types.Count == 0 || filter.Types.Contains(kind);
    }

    static bool MatchesLabels(AssetEntry entry, IReadOnlySet<string> labels) => labels.Count == 0
        || entry.AssetMetadata is { } asset && labels.All(label => asset.Labels.Contains(label));
}
