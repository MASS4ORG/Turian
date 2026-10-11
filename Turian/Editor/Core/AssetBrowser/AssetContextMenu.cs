namespace Turian.Editor.Core;

/// <summary>Enabled states shared by tree, tile and empty-background asset menus.</summary>
public sealed record AssetMenuRules(bool HasSelection, bool Single, bool Writable, bool CanRename,
    bool CanLabel, bool CanPaste, bool CanCreate, bool CanOpen, bool CanReimport);

/// <summary>Derives action availability from the entire selection and destination.</summary>
public static class AssetContextMenu
{
    /// <summary>Evaluates menu actions without mutating the browser or file system.</summary>
    public static AssetMenuRules Rules(IReadOnlyList<AssetEntry> selection, AssetEntry? destination,
        bool canPaste, Func<string, bool> canImport)
    {
        var single = selection.Count == 1;
        var writable = Writable(selection);
        var labelable = writable && selection.All(Labelable);
        var canWrite = destination is { IsDirectory: true, IsReadOnly: false };
        return new AssetMenuRules(selection.Count > 0, single, writable, writable && single, labelable,
            canWrite && canPaste, canWrite, selection.Any(entry => !entry.IsDirectory),
            writable && selection.All(entry => Importable(entry, canImport)));
    }

    static bool Writable(IReadOnlyList<AssetEntry> selection) => selection.Count > 0
        && selection.All(entry => !entry.IsReadOnly && entry.ParentPath is not null);

    static bool Labelable(AssetEntry entry) => !entry.IsDirectory && entry.AssetMetadata is not null;

    static bool Importable(AssetEntry entry, Func<string, bool> canImport) => !entry.IsDirectory && canImport(entry.AbsolutePath);
}
