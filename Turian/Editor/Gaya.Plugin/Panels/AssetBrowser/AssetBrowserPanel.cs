namespace Gaya.Plugin.Turian;

/// <summary>
/// Browses project and brick assets through a shared tree, folder grid and filtered results view.
/// </summary>
sealed partial class AssetBrowserPanel : IPanel, IDisposable
{

    readonly TreeViewState state = new() { MultiSelect = true };
    readonly List<TreeItem> rows = [];

    readonly AssetFileSystem fileSystem;
    readonly BuildManager build;
    readonly SettingsService settings;
    readonly AssetOpenService opener;
    readonly AssetInspectionService inspections;
    readonly NodeInspectorController inspector;
    readonly AssetCreationCatalog creation;
    readonly AssetBrowserSettings browserSettings;
    readonly AssetTypeCatalog types;
    readonly PrefabAuthoring prefabs;
    readonly AssetFileOperations operations;
    readonly BrickAssetTree brickTree;
    readonly ConfirmDialogChrome confirm;
    readonly BricksController bricks;
    readonly AssetRevealService reveal;
    readonly FileDialogChrome? fileDialogs;
    readonly IEditorSettings editorSettings;
    readonly AssetThumbnailCache thumbnails;
    readonly AssetThumbnailRenderer? thumbnailRenderer;
    readonly AssetImporter? importer;
    readonly AssetLabelService? labelService;
    readonly AssetDatabase? database;
    readonly AssetBrowserNavigation navigation = new();
    AssetFavoritesStore? favorites;
    readonly Dictionary<string, string> thumbnailRevisions = new(StringComparer.Ordinal);
    readonly Dictionary<string, AssetEntry[]> children = new(StringComparer.Ordinal);
    readonly Dictionary<string, AssetEntry> byPath = new(StringComparer.Ordinal);
    AssetBrowserViewMode frameMode;
    int scanDirty;
    bool queryDirty = true;
    bool treeDirty;
    bool thumbnailDirty;
    float split = 0.3f;
    IReadOnlyList<AssetEntry> entries = [];
    string? scannedRoot;
    string? inspectedId;
    IReadOnlyList<string> inspectedIds = [];
    Guid pendingReveal;
    bool bricksChanged;

    bool menuOpen;
    Vector2 menuAt;
    AssetEntry? menuEntry;
    Vector2 pointer;
    IClipboard? systemClipboard;

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
    /// <param name="prefabs">Creates prefab variants.</param>
    /// <param name="operations">Renames, deletes, duplicates and pastes as undoable steps.</param>
    /// <param name="bricks">Reports changes to the installed bricks.</param>
    /// <param name="confirm">Asks before an asset is copied out of a brick by dragging.</param>
    /// <param name="build">Supplies the loaded user assemblies a dragged script's component type is found in.</param>
    /// <param name="thumbnailRenderer">Renders queued previews on the GUI thread.</param>
    /// <param name="importer">Reports imported content changes.</param>
    /// <param name="labelService">Persists undoable label edits.</param>
    /// <param name="database">Provides imported thumbnail revision hashes.</param>
    /// <param name="fileDialogs">Selects source files to import into the current folder.</param>
    public AssetBrowserPanel(AssetFileSystem fileSystem, SettingsService settings, AssetOpenService opener,
        AssetInspectionService inspections, NodeInspectorController inspector, AssetRevealService reveal,
        AssetCreationCatalog creation, AssetBrowserSettings browserSettings, IEditorSettings editorSettings,
        AssetTypeCatalog types, AssetPreviewCatalog previews, PrefabAuthoring prefabs, AssetFileOperations operations,
        BricksController bricks, ConfirmDialogChrome confirm, BuildManager build,
        AssetThumbnailRenderer? thumbnailRenderer = null, AssetImporter? importer = null,
        AssetLabelService? labelService = null, AssetDatabase? database = null, FileDialogChrome? fileDialogs = null)
    {
        this.build = build;
        ArgumentNullException.ThrowIfNull(reveal);

        this.fileSystem = fileSystem;
        this.settings = settings;
        this.opener = opener;
        this.inspections = inspections;
        this.inspector = inspector;
        this.creation = creation;
        this.browserSettings = browserSettings;
        this.types = types;
        this.prefabs = prefabs;
        this.operations = operations;
        this.confirm = confirm;
        this.bricks = bricks;
        this.reveal = reveal;
        this.fileDialogs = fileDialogs;
        this.editorSettings = editorSettings;
        this.thumbnailRenderer = thumbnailRenderer;
        this.importer = importer;
        this.labelService = labelService;
        this.database = database;
        thumbnails = new AssetThumbnailCache((entry, size) => thumbnailRenderer?.Render(entry, size));
        brickTree = new BrickAssetTree(fileSystem);
        bricks.Changed += OnBricksChanged;

        reveal.Requested += OnReveal;
        operations.Changed += Refresh;
        fileSystem.PathMoved += OnPathMoved;
        if (importer is not null) importer.AssetsChanged += Refresh;
        editorSettings.Changed += OnEditorSettingsChanged;
    }

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        var root = settings.Settings?.AssetsAbsoluteDir;
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        if (gui.Pass == Pass.Pass1Build)
        {
            PrepareTree(root);
            frameMode = browserSettings.ViewMode;
        }
        if (gui.Pass == Pass.Pass2Render) pointer = gui.Input.MousePosition;
        systemClipboard ??= gui.Platform.Require<IClipboard>();

        using (gui.Node(-1, -1, "assets/browser").Expand().Direction(Axis.Vertical).Enter())
        {
            gui.On<FileDropEvent>(OnFileDrop);
            Toolbar(gui);
            using (gui.Node(-1, -1, "assets/body").Expand().Direction(Axis.Horizontal).Enter()) Body(gui);
        }

        if (gui.Pass == Pass.Pass2Render)
        {
            InspectSelection();
            if (thumbnails.PendingCount > 0) gui.RequestFrame();
            thumbnails.Process();
        }
        gui.CascadeMenu(ref menuOpen, menuAt, BuildContextMenu);
        LabelPrompt(gui);
    }

    void Body(Gui gui)
    {
        var mode = AssetQuery.IsActive(frameFilter) ? AssetBrowserViewMode.Grid : frameMode;
        if (mode == AssetBrowserViewMode.Split)
        {
            using (gui.Node().ExpandWidth(split).ExpandHeight().Enter()) Tree(gui);
            gui.Splitter(ref split, Axis.Horizontal, thickness: 3f, min: 0.15f);
            using (gui.Node().ExpandWidth(1f - split).ExpandHeight().Enter()) Grid(gui);
        }
        else if (mode == AssetBrowserViewMode.Tree) Tree(gui);
        else Grid(gui);
    }

    void Tree(Gui gui) => gui.TreeView(state, rows, StudioControls.Tree(), OnClick, DragPayload, Rename, OnEmptyClick,
        dropAccept: BrowserPayload, onDrop: OnDrop);

    void PrepareTree(string root)
    {
        thumbnails.BeginFrame();
        if (thumbnailDirty) { thumbnails.Clear(); thumbnailDirty = false; }
        if (root != scannedRoot || bricksChanged || Interlocked.Exchange(ref scanDirty, 0) != 0)
        {
            bricksChanged = false;
            Rescan(root);
            Rebuild();
        }

        if (treeDirty) { Rebuild(); treeDirty = false; }

        if (pendingReveal != Guid.Empty) RevealPending();
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

        // Revealed for a reference the inspector shows, so the inspector keeps what it is editing.
        state.SelectedId = target.AbsolutePath;
        inspectedId = target.AbsolutePath;
        inspectedIds = state.SelectedIds;
        state.Reveal();
        NavigateTo(target.IsDirectory ? target.AbsolutePath : target.ParentPath!);
        ClearFilters();
        gridReveal = true;
    }

    string? ParentOf(string path) => byPath.GetValueOrDefault(path)?.ParentPath;

    /// <summary>Re-reads the asset folder.</summary>
    /// <param name="root">The project's assets directory.</param>
    public void Rescan(string root)
    {
        var projectRoot = Path.GetDirectoryName(root)!;
        entries = [new AssetEntry(root, true, null, null),
            .. fileSystem.ScanDirectory(root).Select(entry => entry with { ParentPath = entry.ParentPath ?? root }),
            .. brickTree.Scan(projectRoot)];
        scannedRoot = root;
        byPath.Clear();
        foreach (var entry in entries) byPath[entry.AbsolutePath] = entry;
        BindFavorites(projectRoot, root);
        IndexChildren();
        IndexThumbnails();
        queryDirty = true;
        breadcrumbsDirty = true;
        toolbarDataDirty = true;
    }

    void BindFavorites(string projectRoot, string root)
    {
        if (favorites is null || projectRoot != favoritesProject)
        {
            favoritesProject = projectRoot;
            favorites = new AssetFavoritesStore(projectRoot);
            navigation.Reset(root);
        }
        if (!entries.Any(entry => entry.IsDirectory && entry.AbsolutePath == navigation.Current)) navigation.Visit(root);
    }

    void IndexChildren()
    {
        children.Clear();
        foreach (var group in entries.Where(entry => entry.ParentPath is not null).GroupBy(entry => entry.ParentPath!))
            children[group.Key] = [.. group.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => Path.GetFileName(entry.AbsolutePath), StringComparer.OrdinalIgnoreCase)];
    }

    void IndexThumbnails()
    {
        thumbnails.Clear();
        thumbnailRevisions.Clear();
        foreach (var entry in entries.Where(entry => !entry.IsDirectory))
            thumbnailRevisions[entry.AbsolutePath] = Revision(entry);
    }

    void Rebuild()
    {
        rows.Clear();
        AppendFavorites();
        foreach (var entry in ChildrenOf(null)) Append(entry, depth: 0);
    }

    void AppendFavorites()
    {
        if (browserSettings.ShowFavoritesInTree && favorites is { Paths.Count: > 0 })
        {
            rows.Add(new TreeItem("assets/favorites", "Favorites", 0, true,
                Icon: gui => gui.DrawText(EditorIcons.Star)));
            foreach (var entry in entries.Where(entry => favorites.Paths.Contains(entry.AbsolutePath)))
                rows.Add(new TreeItem(entry.AbsolutePath, DisplayName(entry.AbsolutePath), 1, Icon: IconFor(entry), Tag: entry));
        }
    }

    string? favoritesProject;

    string Revision(AssetEntry entry)
    {
        var file = new FileInfo(entry.AbsolutePath);
        var meta = new FileInfo(entry.AbsolutePath + ".meta");
        var record = RecordFor(entry);
        return $"{file.LastWriteTimeUtc.Ticks}:{file.Length}:{meta.LastWriteTimeUtc.Ticks}:"
            + $"{record?.SourceHash}:{record?.SettingsHash}:{record?.ImporterVersion}";
    }

    AssetRecord? RecordFor(AssetEntry entry)
    {
        if (database is null || entry.AssetMetadata is null) return null;
        database.TryGetAsset(entry.AssetMetadata.Id, out var record);
        return record;
    }

    void OnPathMoved(string source, string destination)
    {
        favorites?.Remap(source, destination);
        navigation.Remap(source, destination);
        state.SetSelection(state.SelectedIds.Select(path => AssetPathSegments.Remap(path, source, destination)),
            state.SelectedId is { } selected ? AssetPathSegments.Remap(selected, source, destination) : null);
    }

    void OnBricksChanged() => bricksChanged = true;

    void OnReveal(Guid assetId) => pendingReveal = assetId;

    /// <inheritdoc />
    public void Dispose()
    {
        operations.Changed -= Refresh;
        bricks.Changed -= OnBricksChanged;
        reveal.Requested -= OnReveal;
        fileSystem.PathMoved -= OnPathMoved;
        editorSettings.Changed -= OnEditorSettingsChanged;
        if (importer is not null) importer.AssetsChanged -= Refresh;
        thumbnails.Dispose();
        thumbnailRenderer?.Dispose();
    }

}
