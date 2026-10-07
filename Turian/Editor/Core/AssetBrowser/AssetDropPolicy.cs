namespace Turian.Editor.Core;

/// <summary>A validated browser drop destination and the entries to transfer into it.</summary>
public sealed record AssetDrop(string Directory, IReadOnlyList<AssetEntry> Entries, bool Copy);

/// <summary>Applies common writable-root and ancestry rules to tree and grid drops.</summary>
public static class AssetDropPolicy
{
    /// <summary>Returns a move or brick-copy request, or null for an invalid or mixed drop.</summary>
    public static AssetDrop? Resolve(AssetEntry target, object payload, IReadOnlyList<AssetEntry> entries)
    {
        if (!target.IsDirectory || target.IsReadOnly) return null;
        var carried = Carried(payload, entries);
        if (!ValidSources(carried, target.AbsolutePath)) return null;
        var copy = carried.All(entry => entry.IsReadOnly);
        if (!copy && carried.Any(entry => entry.IsReadOnly)) return null;
        return new AssetDrop(target.AbsolutePath, carried, copy);
    }

    static bool ValidSources(IReadOnlyList<AssetEntry> carried, string destination) => carried.Count > 0
        && carried.All(entry => entry.ParentPath is not null && !Inside(entry.AbsolutePath, destination));

    static IReadOnlyList<AssetEntry> Carried(object payload, IReadOnlyList<AssetEntry> entries) => payload switch
    {
        ReferenceDragPayload { Entries.Count: > 0 } reference => reference.Entries,
        ReferenceDragPayload { AssetPath: { } path } => [.. entries.Where(entry => entry.AbsolutePath == path)],
        ScriptDragPayload { AssetPath: { } path } => [.. entries.Where(entry => entry.AbsolutePath == path)],
        _ => [],
    };

    static bool Inside(string directory, string path) => path == directory
        || path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
