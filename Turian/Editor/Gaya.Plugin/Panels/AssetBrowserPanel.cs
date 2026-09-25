namespace Gaya.Plugin.Turian;

/// <summary>
/// The project's asset folder as a tree. A click shows the asset's import settings in the inspector,
/// a double click opens it the way its kind says it opens, and a right-click offers rename, delete,
/// copy, paste and the New menu.
/// </summary>
sealed class AssetBrowserPanel : IPanel
{

    readonly TreeViewState state = new();
    readonly List<TreeItem> rows = [];

    readonly AssetFileSystem fileSystem;
    readonly SettingsService settings;
    readonly AssetOpenService opener;
    readonly AssetInspectionService inspections;
    readonly NodeInspectorController inspector;
    readonly AssetCreationCatalog creation;
    readonly AssetBrowserSettings browserSettings;
    readonly AssetTypeCatalog types;
    readonly AssetPreviewCatalog previews;
    readonly Dictionary<string, SKImage?> textureThumbnails = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<AssetEntry> entries = [];
    string? scannedRoot;
    Guid pendingReveal;
    bool showExtensions;

    bool menuOpen;
    Vector2 menuAt;
    AssetEntry? menuEntry;
    Vector2 pointer;

    /// <summary>Creates the panel and listens for reveal requests from elsewhere in the studio.</summary>
    /// <param name="fileSystem">Scans the project's asset folder and performs its file operations.</param>
    /// <param name="settings">Names the folder to scan.</param>
    /// <param name="opener">Performs what opening an asset means for its kind.</param>
    /// <param name="inspections">Resolves what the inspector edits for the selected asset.</param>
    /// <param name="inspector">Shown the selected asset's import settings or payload.</param>
    /// <param name="reveal">Raised by the inspector when a reference field is clicked.</param>
    /// <param name="creation">Fills the New menu.</param>
    /// <param name="browserSettings">Controls asset-name presentation.</param>
    /// <param name="editorSettings">Reports preference edits, so the tree rebuilds when the extensions toggle flips.</param>
    /// <param name="types">Resolves a file's kind, for its default icon when it has no live preview.</param>
    /// <param name="previews">Answers whether a row's asset has a live preview, and of which kind.</param>
    public AssetBrowserPanel(AssetFileSystem fileSystem, SettingsService settings, AssetOpenService opener,
        AssetInspectionService inspections, NodeInspectorController inspector, AssetRevealService reveal,
        AssetCreationCatalog creation, AssetBrowserSettings browserSettings, IEditorSettings editorSettings,
        AssetTypeCatalog types, AssetPreviewCatalog previews)
    {
        ArgumentNullException.ThrowIfNull(reveal);

        this.fileSystem = fileSystem;
        this.settings = settings;
        this.opener = opener;
        this.inspections = inspections;
        this.inspector = inspector;
        this.creation = creation;
        this.browserSettings = browserSettings;
        this.types = types;
        this.previews = previews;
        showExtensions = browserSettings.ShowFileExtensions;

        reveal.Requested += assetId => pendingReveal = assetId;
        editorSettings.Changed += OnEditorSettingsChanged;
    }

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        var root = settings.Settings?.AssetsAbsoluteDir;
        if (string.IsNullOrEmpty(root))
        {
            gui.DrawText("No project loaded.", StudioTheme.Current.Text(12), StudioTheme.Current.InkDim,
                centerInRect: false);
            return;
        }

        if (gui.Pass == Pass.Pass1Build && root != scannedRoot)
        {
            Rescan(root);
            Rebuild();
        }

        if (gui.Pass == Pass.Pass1Build && pendingReveal != Guid.Empty) RevealPending();
        if (gui.Pass == Pass.Pass2Render) pointer = gui.Input.MousePosition;

        gui.TreeView(state, rows, StudioControls.Tree(), OnClick, DragPayload, Rename, OnEmptyClick);

        gui.CascadeMenu(ref menuOpen, menuAt, BuildContextMenu);
    }

    /// <summary>
    /// Selects the asset a reveal asked for and opens every folder above it, so the row is one the
    /// tree can scroll to.
    /// </summary>
    void RevealPending()
    {
        var target = entries.FirstOrDefault(entry => entry.AssetMetadata?.Id == pendingReveal);
        pendingReveal = Guid.Empty;
        if (target is null) return;

        for (var parent = target.ParentPath; parent is not null; parent = ParentOf(parent))
            state.SetExpanded(parent, true);

        state.SelectedId = target.AbsolutePath;
        state.Reveal();
    }

    string? ParentOf(string path) =>
        entries.FirstOrDefault(entry => entry.AbsolutePath == path)?.ParentPath;

    /// <summary>Re-reads the asset folder.</summary>
    /// <param name="root">The project's assets directory.</param>
    public void Rescan(string root)
    {
        entries = fileSystem.ScanDirectory(root);
        scannedRoot = root;
        textureThumbnails.Clear();
    }

    void Rebuild()
    {
        rows.Clear();
        foreach (var entry in ChildrenOf(null)) Append(entry, depth: 0);
    }

    /// <summary>A file row carries its asset id, so it can be dropped on a reference field.</summary>
    static object? DragPayload(TreeItem item) =>
        item.Tag is not AssetEntry { IsDirectory: false } entry ? null
        : entry.AbsolutePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? ScriptPayload(entry)
            : entry.AssetMetadata is { } asset ? new ReferenceDragPayload(asset.Id, item.Label) : null;

    static object? ScriptPayload(AssetEntry entry)
    {
        if (!entry.AbsolutePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return null;

        var name = Path.GetFileNameWithoutExtension(entry.AbsolutePath);
        foreach (var assembly in BuildManager.Instance.LoadedAssemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = [.. ex.Types.Where(t => t is not null).Cast<Type>()]; }
            catch { continue; }

            var component = types.FirstOrDefault(type =>
                typeof(Component).IsAssignableFrom(type) && type.Name.Equals(name, StringComparison.Ordinal));
            if (component is not null) return new ScriptDragPayload(component, name);
        }

        return null;
    }

    void Append(AssetEntry entry, int depth)
    {
        rows.Add(new TreeItem(
            entry.AbsolutePath,
            DisplayName(entry.AbsolutePath),
            depth,
            entry.IsDirectory,
            Icon: IconFor(entry),
            Tag: entry,
            Tint: entry.IsDirectory ? StudioTheme.Current.Folder : null));

        if (!entry.IsDirectory) return;

        foreach (var child in ChildrenOf(entry.AbsolutePath)) Append(child, depth + 1);
    }

    /// <summary>
    /// What a row draws before its label: a folder glyph, a real thumbnail for a texture (the source
    /// file decoded directly — cheap enough for a small icon, and exactly what its preview would show
    /// anyway), or the kind's default glyph for anything else, including asset types with a live
    /// preview too costly to render per row today (a material or a model — see <see cref="AssetPreviewCatalog"/>).
    /// </summary>
    Action<Gui>? IconFor(AssetEntry entry)
    {
        var theme = StudioTheme.Current;
        var size = theme.Scale(theme.RowHeight) - 4f;

        if (entry.IsDirectory)
            return gui => gui.DrawText("📁", theme.Text(13), theme.Folder);

        var kind = types.Resolve(entry.AbsolutePath);
        var hasTexturePreview = entry.AssetMetadata is { } asset &&
                                 previews.GetProvider(asset.GetType()) is ITexturePreviewProvider;

        if (hasTexturePreview && ThumbnailFor(entry.AbsolutePath) is { } thumbnail)
            return gui => gui.Image(thumbnail, size, size);

        var glyph = kind?.DefaultIcon ?? "📄";
        return gui => gui.DrawText(glyph, theme.Text(13), theme.InkDim);
    }

    /// <summary>
    /// Decodes a texture file's own bytes for its browser icon — no GPU round-trip, since the source
    /// image already is the preview. Cached per path; cleared on <see cref="Rescan"/> so a reimported
    /// or replaced file picks up its new contents.
    /// </summary>
    SKImage? ThumbnailFor(string path)
    {
        if (textureThumbnails.TryGetValue(path, out var cached)) return cached;

        SKImage? image;
        try { image = SKImage.FromEncodedData(path); }
        catch { image = null; }

        textureThumbnails[path] = image;
        return image;
    }

    IEnumerable<AssetEntry> ChildrenOf(string? parentPath) =>
        entries
            .Where(entry => entry.ParentPath == parentPath)
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => Path.GetFileName(entry.AbsolutePath), StringComparer.OrdinalIgnoreCase);

    void OnClick(TreeViewEvent e)
    {
        if (e.Item.Tag is not AssetEntry entry) return;

        if (e.Button == MouseButton.Right)
        {
            menuEntry = entry;
            menuAt = pointer;
            menuOpen = true;
            return;
        }

        if (e.Button != MouseButton.Left || entry.IsDirectory) return;

        // Folding is the tree's own business; a double click here opens the asset.
        if (e.ClickCount >= 2) opener.Open(entry);
        else inspector.Select(inspections.Inspect(entry));
    }

    void OnEmptyClick(MouseButton button)
    {
        if (button != MouseButton.Right) return;

        menuEntry = null;
        menuAt = pointer;
        menuOpen = true;
    }

    void BuildContextMenu(FlyoutBuilder menu)
    {
        var entry = menuEntry;

        menu.Item("Rename", () => BeginRename(entry), enabled: entry is not null);
        menu.Item("Delete", () => Delete(entry), enabled: entry is not null);
        menu.Separator();
        menu.Item("Copy", () => Copy(entry), enabled: entry is not null);
        menu.Item("Paste", () => Paste(entry), enabled: fileSystem.CanPaste());
        menu.Separator();
        menu.Submenu("New", submenu => BuildNewMenu(submenu, creation.Kinds, depth: 0));
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

    /// <summary>Whether a row is selected, so an edit has a target.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>Deletes the selected asset or folder. What the panel's Delete shortcut runs.</summary>
    public void DeleteSelected() => Delete(Selected);

    /// <summary>Starts the in-place rename of the selected row. What the panel's F2 shortcut runs.</summary>
    public void RenameSelected() => BeginRename(Selected);

    /// <summary>Copies the selected asset beside itself. What the panel's Duplicate shortcut runs.</summary>
    public void DuplicateSelected()
    {
        if (Selected is not { } entry) return;
        if ((entry.ParentPath ?? settings.Settings?.AssetsAbsoluteDir) is not { } directory) return;

        fileSystem.Duplicate(entry.AbsolutePath, directory, entry.IsDirectory);
        Refresh();
    }

    /// <summary>The row the tree has selected, or null when nothing is.</summary>
    AssetEntry? Selected =>
        entries.FirstOrDefault(entry => entry.AbsolutePath == state.SelectedId);

    void BeginRename(AssetEntry? entry)
    {
        if (entry is null) return;

        state.BeginRename(entry.AbsolutePath, DisplayName(entry.AbsolutePath));
    }

    void Rename(TreeItem item, string name)
    {
        if (item.Tag is not AssetEntry entry || string.IsNullOrWhiteSpace(name)) return;

        if (!entry.IsDirectory && !browserSettings.ShowFileExtensions &&
            string.IsNullOrEmpty(Path.GetExtension(name)))
            name += Path.GetExtension(entry.AbsolutePath);

        fileSystem.Rename(entry.AbsolutePath, entry.IsDirectory, name);
        Refresh();
    }

    string DisplayName(string path)
    {
        var name = Path.GetFileName(path);
        return browserSettings.ShowFileExtensions ? name : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>
    /// Rebuilds the tree when the file-extension preference changes. The chrome's toggle reports the
    /// page, which lands here through <see cref="IEditorSettings.Changed"/>; anything else on the
    /// settings file that changed value to the same extensions flag is ignored.
    /// </summary>
    void OnEditorSettingsChanged()
    {
        if (browserSettings.ShowFileExtensions == showExtensions) return;

        showExtensions = browserSettings.ShowFileExtensions;
        Rebuild();
    }

    void Delete(AssetEntry? entry)
    {
        if (entry is null) return;

        fileSystem.Delete(entry.AbsolutePath);
        Refresh();
    }

    void Copy(AssetEntry? entry)
    {
        if (entry is null) return;

        fileSystem.Copy(entry.AbsolutePath);
    }

    void Paste(AssetEntry? entry)
    {
        if (DirectoryFor(entry) is not { } directory) return;

        fileSystem.Paste(directory);
        Refresh();
    }

    void Create(AssetCreationKind kind)
    {
        if (DirectoryFor(menuEntry) is not { } directory) return;

        creation.Create(kind, directory);
        Refresh();
    }

    /// <summary>The folder an operation targets: the row itself when it is one, otherwise its parent.</summary>
    string? DirectoryFor(AssetEntry? entry) =>
        entry is null
            ? settings.Settings?.AssetsAbsoluteDir
            : entry.IsDirectory ? entry.AbsolutePath : entry.ParentPath ?? settings.Settings?.AssetsAbsoluteDir;

    void Refresh()
    {
        if (scannedRoot is not { } root) return;

        Rescan(root);
        Rebuild();
    }
}
