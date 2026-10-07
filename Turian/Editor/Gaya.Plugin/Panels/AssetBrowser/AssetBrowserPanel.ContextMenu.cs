namespace Gaya.Plugin.Turian;

sealed partial class AssetBrowserPanel
{
    void BuildContextMenu(FlyoutBuilder menu)
    {
        var entry = menuEntry;

        var selection = entry is null ? [] : SelectedEntries.ToArray();
        var destination = entries.FirstOrDefault(candidate => candidate.AbsolutePath == DirectoryFor(entry));
        var rules = AssetContextMenu.Rules(selection, destination, CanPaste,
            path => importer?.ImporterFor(path) is not null);
        BuildOpenMenu(menu, entry, selection, rules, destination is not null);
        BuildTransferMenu(menu, entry, selection, rules);
        BuildMetadataMenu(menu, selection, rules);
        BuildCreationMenu(menu, entry);
        menu.Item("Import…", () => ImportFile(DirectoryFor(entry)!), enabled: rules.CanCreate && fileDialogs is not null);
        menu.Item("Reimport", () => Reimport(selection), enabled: rules.CanReimport);
        menu.Item("Refresh thumbnails", () => thumbnailDirty = true);
        menu.Item("Refresh", Refresh);
    }

    void BuildOpenMenu(FlyoutBuilder menu, AssetEntry? entry, IReadOnlyList<AssetEntry> selection, AssetMenuRules rules,
        bool hasDestination)
    {
        menu.Item(selection.Count > 1 ? "Open all selected" : "Open", () =>
        {
            foreach (var selected in selection) Activate(selected);
        }, enabled: rules.HasSelection);
        menu.Item("Open externally", () =>
        {
            foreach (var selected in selection) opener.OpenExternally(selected.AbsolutePath);
        }, enabled: rules.CanOpen);
        var showPath = entry?.AbsolutePath ?? navigation.Current!;
        menu.Item("Show in file manager", () => opener.ShowInFileManager(showPath),
            enabled: hasDestination);
        menu.Item("Reveal in browser", () => RevealEntry(entry), enabled: rules.Single);
        menu.Separator();
    }

    void BuildTransferMenu(FlyoutBuilder menu, AssetEntry? entry, IReadOnlyList<AssetEntry> selection, AssetMenuRules rules)
    {
        menu.Item("Rename", () => BeginRename(entry), "F2", rules.CanRename);
        menu.Item("Delete", DeleteSelected, "Del", rules.Writable);
        menu.Item("Duplicate", DuplicateSelected, "Ctrl+D", rules.Writable);
        menu.Separator();
        menu.Item("Cut", CutSelected, "Ctrl+X", rules.Writable);
        menu.Item("Copy", CopySelected, "Ctrl+C", rules.HasSelection);
        menu.Item("Paste", () => PasteSelection(entry), "Ctrl+V", rules.CanPaste);
        menu.Item("Select all", SelectAll, "Ctrl+A");
        menu.Separator();
    }

    void BuildMetadataMenu(FlyoutBuilder menu, IReadOnlyList<AssetEntry> selection, AssetMenuRules rules)
    {
        menu.Item("Copy Name", () => CopyText(selection.Select(selected => Path.GetFileName(selected.AbsolutePath))),
            enabled: rules.HasSelection);
        menu.Item("Copy Path", () => CopyText(selection.Select(selected => selected.AbsolutePath)),
            "Ctrl+Alt+C", rules.HasSelection);
        menu.Item("Copy Relative Path", () => CopyText(selection.Select(selected =>
            Path.GetRelativePath(favoritesProject!, selected.AbsolutePath).Replace('\\', '/'))),
            "Ctrl+Alt+Shift+C", rules.HasSelection);
        menu.Item("Copy Asset ID", () => CopyText(selection.Select(selected => selected.AssetMetadata!.Id.ToString())),
            enabled: rules.HasSelection && selection.All(selected => selected.AssetMetadata is not null));
        menu.Separator();
        menu.Submenu("Labels", submenu => BuildLabelMenu(submenu, selection), enabled: rules.CanLabel && labelService is not null);
        var allFavorite = selection.Count > 0 && selection.All(selected => favorites!.Paths.Contains(selected.AbsolutePath));
        menu.Item(allFavorite ? "Remove Favorite" : "Add Favorite", () => SetFavorites(selection, !allFavorite),
            enabled: rules.HasSelection);
    }

    void BuildCreationMenu(FlyoutBuilder menu, AssetEntry? entry)
    {
        menu.Submenu("New", submenu => BuildNewMenu(submenu, creation.Kinds, depth: 0),
            enabled: entries.Any(candidate => candidate.AbsolutePath == DirectoryFor(entry) && !candidate.IsReadOnly));

        if (entry is { IsDirectory: false, IsReadOnly: false } prefab
            && string.Equals(Path.GetExtension(prefab.AbsolutePath), ".prefab", StringComparison.OrdinalIgnoreCase))
            menu.Item("Create Prefab Variant", () => CreateVariant(prefab));
    }

    void CreateVariant(AssetEntry prefab)
    {
        if (prefabs.CreateVariant(prefab.AbsolutePath) is null)
            Log.Logger.LogWarning("Could not create a variant of {Prefab}", prefab.AbsolutePath);
        Refresh();
    }

    /// <summary>
    /// Emits one level of the New menu: kinds whose path ends here become items, and the rest are
    /// gathered by their next path segment into a submenu that recurses.
    /// </summary>
    void BuildNewMenu(FlyoutBuilder menu, IReadOnlyList<AssetCreationKind> kinds, int depth)
    {
        foreach (var kind in kinds.Where(k => Segments(k).Length == depth + 1))
        {
            var created = kind;
            menu.Item(Segments(kind)[depth], () => Create(created));
        }

        foreach (var group in kinds.Where(k => Segments(k).Length > depth + 1)
                     .GroupBy(k => Segments(k)[depth], StringComparer.Ordinal))
        {
            var nested = group.ToList();
            menu.Submenu(group.Key, sub => BuildNewMenu(sub, nested, depth + 1));
        }
    }

    static string[] Segments(AssetCreationKind kind) =>
        kind.MenuPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    void RevealEntry(AssetEntry? entry)
    {
        if (entry is null) return;
        NavigateTo(entry.IsDirectory ? entry.AbsolutePath : entry.ParentPath!);
        ClearFilters();
        state.SelectedId = entry.AbsolutePath;
        state.Reveal();
        gridReveal = true;
    }

    void Reimport(IEnumerable<AssetEntry> selection)
    {
        foreach (var entry in selection) importer?.ReimportNow(entry.AbsolutePath);
        Refresh();
    }

    void ImportFile(string directory) => fileDialogs?.Show(new FileDialogRequest
    {
        Mode = FileDialogMode.OpenFile,
        Title = "Import asset",
        ConfirmLabel = "Import",
        StartPath = directory,
        Validate = path => importer?.ImporterFor(path) is null ? "No importer supports this file." : null,
        OnComplete = path =>
        {
            if (path is null) return;
            var copied = operations.Duplicate(path, directory, isDirectory: false);
            if (copied is not null) importer?.ReimportNow(copied);
            Refresh();
        },
    });

    void CopyText(IEnumerable<string> values) => systemClipboard?.SetClipboardText(string.Join(Environment.NewLine, values));

    void SetFavorites(IEnumerable<AssetEntry> selection, bool value)
    {
        foreach (var entry in selection) favorites!.Set(entry.AbsolutePath, value);
        QueryChanged();
        treeDirty = true;
    }

    void BuildLabelMenu(FlyoutBuilder menu, IReadOnlyList<AssetEntry> selection)
    {
        foreach (var label in AssetLabelService.Collect(entries))
            menu.CheckItem(label, () => selection.All(entry => entry.AssetMetadata?.Labels.Contains(label) == true),
                present => SetLabel(selection, label, present));
        menu.Item("New label…", () => { labelSelection = selection; newLabel = ""; labelPromptOpen = true; });
    }

    void SetLabel(IReadOnlyList<AssetEntry> selection, string label, bool present)
    {
        labelService?.Set(selection, label, present);
        Refresh();
    }

    bool labelPromptOpen;
    string newLabel = "";
    IReadOnlyList<AssetEntry> labelSelection = [];

    void LabelPrompt(Gui gui) => gui.Popup(ref labelPromptOpen, () =>
    {
        newLabel = gui.TextInput(newLabel, width: 0, placeholder: "Label name", id: "assets/newLabel");
        if (StudioControls.SmallTextButton(gui, "Add label", "assets/addLabel", 100))
        {
            SetLabel(labelSelection, newLabel, true);
            labelPromptOpen = false;
        }
    }, width: 280, height: 90, title: "Asset label");

}
