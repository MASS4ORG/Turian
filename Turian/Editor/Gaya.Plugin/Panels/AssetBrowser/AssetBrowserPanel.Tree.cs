namespace Gaya.Plugin.Turian;

sealed partial class AssetBrowserPanel
{
    void Append(AssetEntry entry, int depth)
    {
        rows.Add(new TreeItem(
            entry.AbsolutePath,
            entry.AbsolutePath == Path.Combine(settings.Settings!.ProjectAbsoluteDir, Packages.ProjectManifest.DirectoryName)
                ? "Bricks" : DisplayName(entry.AbsolutePath),
            depth,
            entry.IsDirectory,
            Icon: IconFor(entry),
            Tag: entry,
            Tint: entry.IsDirectory ? ThemeTokens.Current.Folder : null));

        if (!entry.IsDirectory) return;

        foreach (var child in ChildrenOf(entry.AbsolutePath)) Append(child, depth + 1);
    }

    Action<Gui> IconFor(AssetEntry entry) => gui => DrawPreview(gui, entry,
        ThemeTokens.Current.Scale(ThemeTokens.Current.RowHeight) - 4f);

    void DrawPreview(Gui gui, AssetEntry entry, float size)
    {
        TrackFileDropFolder(gui, entry);
        var image = !entry.IsDirectory
            ? thumbnails.RequestImage(entry, thumbnailRevisions.GetValueOrDefault(entry.AbsolutePath, "")) : null;
        if (image is not null)
        {
            var scale = size / Math.Max(image.Width, image.Height);
            gui.Image(image, image.Width * scale, image.Height * scale);
        }
        else DrawAssetIcon(gui, entry, size);
    }

    void DrawAssetIcon(Gui gui, AssetEntry entry, float size)
    {
        var theme = ThemeTokens.Current;
        var glyph = entry.IsDirectory ? EditorIcons.Folder : types.Resolve(entry.AbsolutePath)?.DefaultIcon ?? EditorIcons.File;
        gui.DrawText(glyph, size * 0.8f, entry.IsDirectory ? theme.Folder : theme.InkDim);
    }

    IEnumerable<AssetEntry> ChildrenOf(string? parentPath) => parentPath is null
        ? entries.Where(entry => entry.ParentPath is null)
        : children.GetValueOrDefault(parentPath) ?? [];

    void OnClick(TreeViewEvent e)
    {
        if (e.Item.Id == "assets/favorites")
        {
            favoritesOnly = true;
            QueryChanged();
            return;
        }
        if (e.Item.Tag is not AssetEntry entry) return;

        if (e.Button == MouseButton.Right) { TreeContextMenu(entry); return; }

        if (e.Button != MouseButton.Left) return;
        if (entry.IsDirectory)
        {
            NavigateTo(entry.AbsolutePath);
            return;
        }

        TreeActivation(entry, e.ClickCount);
    }

    void TreeActivation(AssetEntry entry, int clicks)
    {
        if (clicks >= 2 && !entry.IsReadOnly) opener.Open(entry);
        else InspectSelection();
    }

    void TreeContextMenu(AssetEntry entry)
    {
        if (!state.SelectedIds.Contains(entry.AbsolutePath)) state.SelectedId = entry.AbsolutePath;
        InspectSelection();
        menuEntry = entry;
        menuAt = pointer;
        menuOpen = true;
    }

    void OnEmptyClick(MouseButton button)
    {
        if (button != MouseButton.Right) return;

        menuEntry = null;
        menuAt = pointer;
        menuOpen = true;
    }

}
