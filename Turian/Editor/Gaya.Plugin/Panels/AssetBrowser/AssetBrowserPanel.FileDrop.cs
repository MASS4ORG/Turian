namespace Gaya.Plugin.Turian;

sealed partial class AssetBrowserPanel
{
    void TrackFileDropFolder(Gui gui, AssetEntry entry)
    {
        if (entry.IsDirectory)
            gui.CurrentNode.Parent?.On<FileDropEvent>(drop => ImportDroppedFiles(entry, drop));
    }

    void OnFileDrop(FileDropEvent drop)
    {
        if (byPath.GetValueOrDefault(navigation.Current ?? "") is { } destination)
            ImportDroppedFiles(destination, drop);
    }

    void ImportDroppedFiles(AssetEntry destination, FileDropEvent drop)
    {
        if (new AssetExternalImport(operations).Import(destination, drop.Paths)) Refresh();
        drop.StopPropagation();
    }
}
