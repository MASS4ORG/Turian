namespace Gaya.Plugin.Turian;

sealed partial class AssetBrowserPanel
{
    /// <summary>Whether a row is selected, so an edit has a target.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>Puts the selected row's path on the system clipboard. What the panel's Copy Path shortcuts run.</summary>
    /// <param name="relative">Whether the path starts at the project folder instead of the file system root.</param>
    public void CopySelectedPath(bool relative) => CopyPath(Selected, relative);

    void CopyPath(AssetEntry? entry, bool relative)
    {
        if (entry is null || systemClipboard is null) return;

        var path = relative && settings.Settings?.ProjectAbsoluteDir is { } project
            ? Path.GetRelativePath(project, entry.AbsolutePath).Replace('\\', '/')
            : entry.AbsolutePath;
        systemClipboard.SetClipboardText(path);
    }

    /// <summary>Deletes the selected asset or folder. What the panel's Delete shortcut runs.</summary>
    public void DeleteSelected()
    {
        if (operations.DeleteMany(SelectedEntries))
        {
            state.SetSelection([]);
            InspectSelection();
        }
    }

    /// <summary>Starts the in-place rename of the selected row. What the panel's F2 shortcut runs.</summary>
    public void RenameSelected() => BeginRename(Selected);

    /// <summary>Copies the selected asset beside itself. What the panel's Duplicate shortcut runs.</summary>
    public void DuplicateSelected()
    {
        operations.DuplicateMany(SelectedEntries);
    }

    IReadOnlyList<AssetEntry> clipboardEntries = [];
    bool clipboardCut;

    /// <summary>Whether the internal clipboard has transferable assets.</summary>
    public bool CanPaste => clipboardEntries.Count > 0
        && clipboardEntries.All(entry => File.Exists(entry.AbsolutePath) || Directory.Exists(entry.AbsolutePath))
        || fileSystem.CanPaste();

    /// <summary>Copies selected paths into the asset clipboard.</summary>
    public void CopySelected()
    {
        clipboardEntries = SelectedEntries;
        clipboardCut = false;
        if (clipboardEntries.Count == 1) fileSystem.Copy(clipboardEntries[0].AbsolutePath);
    }

    /// <summary>Cuts writable selected assets for an undoable paste.</summary>
    public void CutSelected()
    {
        clipboardEntries = AssetFileOperations.Roots(SelectedEntries);
        clipboardCut = true;
        if (clipboardEntries.Count == 1) fileSystem.Cut(clipboardEntries[0].AbsolutePath);
    }

    /// <summary>Pastes into the selected directory or the current folder.</summary>
    public void PasteSelected() => PasteSelection(Selected);

    void PasteSelection(AssetEntry? entry)
    {
        var directory = DirectoryFor(entry);
        if (directory is null || entries.FirstOrDefault(candidate => candidate.AbsolutePath == directory)?.IsReadOnly != false) return;
        if (clipboardEntries.Count <= 1) { operations.Paste(directory); return; }
        if (clipboardCut)
        {
            if (operations.MoveMany(clipboardEntries, directory)) clipboardEntries = [];
        }
        else operations.CopyIntoMany(clipboardEntries, directory);
    }

    /// <summary>Selects every asset currently displayed by the focused view.</summary>
    public void SelectAll()
    {
        var visible = frameMode == AssetBrowserViewMode.Tree && !AssetQuery.IsActive(frameFilter)
            ? rows.Where(row => row.Tag is AssetEntry).Select(row => row.Id) : gridIds;
        state.SetSelection(visible);
    }

    /// <summary>The row the tree has selected, or null when nothing is.</summary>
    AssetEntry? Selected =>
        state.SelectedId is { } id ? byPath.GetValueOrDefault(id) : null;

    IReadOnlyList<AssetEntry> SelectedEntries =>
        [.. state.SelectedIds.Select(id => byPath.GetValueOrDefault(id))
            .OfType<AssetEntry>()];

    void InspectSelection()
    {
        if (state.SelectedId == inspectedId && state.SelectedIds.SequenceEqual(inspectedIds)) return;
        inspectedId = state.SelectedId;
        inspectedIds = state.SelectedIds;
        var selected = SelectedEntries.Where(entry => !entry.IsDirectory)
            .Select(entry => inspections.Inspect(entry)).OfType<AssetInspection>().ToArray();
        inspector.SelectMany(selected, selected.FirstOrDefault(item => item.AbsolutePath == inspectedId));
    }

    void BeginRename(AssetEntry? entry)
    {
        if (entry is null || entry.IsReadOnly || entry.ParentPath is null) return;

        state.BeginRename(entry.AbsolutePath, DisplayName(entry.AbsolutePath));
        renameFocusRequested = true;
    }

    bool renameFocusRequested;

    void Rename(TreeItem item, string name)
    {
        if (item.Tag is not AssetEntry { IsReadOnly: false } entry || string.IsNullOrWhiteSpace(name)) return;

        if (!entry.IsDirectory && !browserSettings.ShowFileExtensions &&
            string.IsNullOrEmpty(Path.GetExtension(name)))
            name += Path.GetExtension(entry.AbsolutePath);

        operations.Rename(entry.AbsolutePath, entry.IsDirectory, name);
    }

    string DisplayName(string path)
    {
        var name = Path.GetFileName(path);
        return browserSettings.ShowFileExtensions || byPath.GetValueOrDefault(path)?.IsDirectory == true
            ? name : Path.GetFileNameWithoutExtension(name);
    }

    void OnEditorSettingsChanged() => treeDirty = true;

    void Create(AssetCreationKind kind)
    {
        if (DirectoryFor(menuEntry) is not { } directory) return;

        creation.Create(kind, directory);
        Refresh();
    }

    /// <summary>The folder an operation targets: the row itself when it is one, otherwise its parent.</summary>
    string? DirectoryFor(AssetEntry? entry) =>
        entry is null
            ? navigation.Current ?? settings.Settings?.AssetsAbsoluteDir
            : entry.IsDirectory ? entry.AbsolutePath : entry.ParentPath ?? settings.Settings?.AssetsAbsoluteDir;

    void Refresh()
    {
        Interlocked.Exchange(ref scanDirty, 1);
    }
}
