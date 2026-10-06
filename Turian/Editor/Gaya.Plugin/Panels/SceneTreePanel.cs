namespace Gaya.Plugin.Turian;

/// <summary>
/// The scene hierarchy: the open scene's nodes, expandable, with the selected one driving the
/// inspector. A right-click offers rename, delete, copy, paste and node creation.
/// </summary>
sealed class SceneTreePanel : IPanel
{

    readonly TreeViewState state = new() { MultiSelect = true };
    readonly List<TreeItem> rows = [];
    readonly Dictionary<Node, string> keysByNode = [];
    readonly Dictionary<string, Node> nodesByKey = [];

    Node? syncedSelection;
    IReadOnlyList<Node> syncedNodes = [];

    readonly SceneTreeController sceneTree;
    readonly NodeInspectorController inspector;
    readonly PrefabAuthoring prefabs;
    readonly PrefabStage prefabStage;
    readonly SettingsService settings;
    readonly UndoService undo;
    readonly PrefabOverrideOperations overrides;
    readonly ConfirmDialogChrome confirm;
    readonly StudioLocalization localization;
    readonly PrefabLinkClassifier prefabLinks;

    Node? builtFor;
    bool stale = true;

    bool menuOpen;
    Vector2 menuAt;
    Node? menuNode;
    Node? clipboard;
    Vector2 pointer;

    /// <summary>Creates the panel and follows the controller's scene changes.</summary>
    /// <param name="sceneTree">Supplies the hierarchy and receives selection.</param>
    /// <param name="inspector">Told what the user selected.</param>
    /// <param name="assets">Watched so an edit to a node on screen refreshes its row.</param>
    /// <param name="prefabs">Saves a node as a prefab.</param>
    /// <param name="prefabStage">Opens an instance's prefab in prefab mode.</param>
    /// <param name="settings">Locates the open scene's folder, where a new prefab is saved.</param>
    /// <param name="undo">Records every hierarchy edit made here.</param>
    /// <param name="overrides">Unpacks prefab instances.</param>
    /// <param name="confirm">Asks before an edit unpacks a prefab instance.</param>
    /// <param name="localization">Translates the questions asked.</param>
    /// <param name="database">The asset database prefab links are read from.</param>
    public SceneTreePanel(SceneTreeController sceneTree, NodeInspectorController inspector, AssetManager assets,
        PrefabAuthoring prefabs, PrefabStage prefabStage, SettingsService settings, UndoService undo,
        PrefabOverrideOperations overrides, ConfirmDialogChrome confirm, StudioLocalization localization,
        AssetDatabase database)
    {
        this.sceneTree = sceneTree;
        prefabLinks = new PrefabLinkClassifier(id => PrefabInstances.ReadPrefabJson(database, id));
        this.inspector = inspector;
        this.prefabs = prefabs;
        this.prefabStage = prefabStage;
        this.settings = settings;
        this.undo = undo;
        this.overrides = overrides;
        this.confirm = confirm;
        this.localization = localization;

        // SceneLoaded covers opening, closing, reloading and the play-mode hierarchy swap; NodeUpdated
        // covers an edit to a node already on screen, such as a rename from the inspector.
        sceneTree.SceneLoaded += _ => stale = true;
        assets.NodeUpdated += _ => stale = true;
    }

    /// <summary>A node row carries its node id, so it can be dropped on a reference field.</summary>
    static object? DragPayload(TreeItem item) =>
        item.Tag is Node node ? new ReferenceDragPayload(node.Id, node.Name) : null;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        var root = sceneTree.CurrentSceneRoot;
        if (root is null)
        {
            return;
        }

        if (gui.Pass == Pass.Pass2Render) pointer = gui.Input.MousePosition;

        // Flattening a scene the size of Bistro allocates thousands of rows, so it only happens when
        // the hierarchy changes rather than every frame.
        if (gui.Pass == Pass.Pass1Build) PrepareTree(root);

        // The selection flows both ways: the controller wins when something else changed it, and the
        // tree wins when the keyboard moved it. Pushing the controller's value in unconditionally
        // undid every arrow-key move on the next frame.
        if (gui.Pass == Pass.Pass1Build) PushSelection();

        gui.TreeView(state, rows, StudioControls.Tree(), OnClick, DragPayload, Rename, OnEmptyClick,
            dropAccept: CanDrop, onDrop: Drop);

        if (gui.Pass == Pass.Pass2Render) PullSelection();
        gui.CascadeMenu(ref menuOpen, menuAt, BuildContextMenu);
    }

    void PrepareTree(Node root)
    {
        if (stale || !ReferenceEquals(builtFor, root)) Rebuild(root);
    }

    void PushSelection()
    {
        if (!ReferenceEquals(inspector.SelectedNode, syncedSelection)
            || !inspector.SelectedNodes.SequenceEqual(syncedNodes))
        {
            syncedSelection = inspector.SelectedNode;
            syncedNodes = inspector.SelectedNodes;
            state.SetSelection(syncedNodes.Select(node => keysByNode.GetValueOrDefault(node)).OfType<string>(),
                syncedSelection is null ? null : keysByNode.GetValueOrDefault(syncedSelection));
            if (state.SelectedId is { } revealed) Reveal(revealed);
        }

    }

    bool CanDrop(object payload) => payload is ScriptDragPayload drop
                    && typeof(Component).IsAssignableFrom(drop.ComponentType)
                || payload is ReferenceDragPayload dragged && sceneTree.FindNodeById(dragged.Id) is not null;

    void Drop(TreeItem item, object payload)
    {
        if (item.Tag is Node node && payload is ScriptDragPayload drop)
        {
            DropComponent(node, drop.ComponentType);
        }

        // A node row dropped on another row moves under it.
        if (item.Tag is Node target && payload is ReferenceDragPayload dragged
            && sceneTree.FindNodeById(dragged.Id) is { } dropped)
            ReparentSelection(dropped, target);
    }

    void DropComponent(Node node, Type type)
    {
        if (!inspector.SelectedNodes.Contains(node)) inspector.Select(node);
        undo.BeginGesture();
        try
        {
            foreach (var selected in inspector.SelectedNodes) undo.RecordObject(selected, "Add Component");
            if (inspector.AddComponents(inspector.SelectedNodes, type)) Invalidate();
        }
        finally { undo.EndGesture(); }
    }

    /// <summary>Forces a rebuild; call after editing the hierarchy from this panel.</summary>
    public void Invalidate() => stale = true;

    /// <summary>
    /// Opens every row above <paramref name="key"/> and scrolls to it, for a selection the user did not
    /// make here. Keys are the path of child indices from the root, so an ancestor is a prefix.
    /// </summary>
    void Reveal(string key)
    {
        for (var cut = key.LastIndexOf('/'); cut > 0; cut = key.LastIndexOf('/', cut - 1))
            state.SetExpanded(key[..cut], true);

        state.Reveal();
    }

    void Rebuild(Node root)
    {
        rows.Clear();
        keysByNode.Clear();
        nodesByKey.Clear();
        prefabLinks.Reset();
        Append(root, depth: 0, key: "0");

        builtFor = root;
        stale = false;
        syncedNodes = [];
    }

    /// <summary>
    /// Keys are the path of child indices from the root. Node ids cannot be used: imported scenes have
    /// been seen carrying the same id on several nodes, which would collapse them into a single row.
    /// </summary>
    void Append(Node node, int depth, string key)
    {
        keysByNode[node] = key;
        nodesByKey[key] = node;
        var link = prefabLinks.Classify(node);
        rows.Add(new TreeItem(
            key,
            DisplayName(node),
            depth,
            node.Children.Count > 0,
            Icon: PrefabIcon(link),
            Tag: node,
            Tint: Tint(node, link)));

        var children = node.Children.ToList();
        for (var i = 0; i < children.Count; i++) Append(children[i], depth + 1, $"{key}/{i}");
    }

    void OnClick(TreeViewEvent e)
    {
        if (e.Item.Tag is not Node node) return;

        if (e.Button == MouseButton.Right)
        {
            if (!inspector.SelectedNodes.Contains(node)) Select(node);
            menuNode = node;
            menuAt = pointer;
            menuOpen = true;
            return;
        }

        if (e.Button != MouseButton.Left) return;

        PullSelection();
    }

    void OnEmptyClick(MouseButton button)
    {
        if (button != MouseButton.Right) return;

        menuNode = inspector.SelectedNode ?? sceneTree.CurrentSceneRoot;
        menuAt = pointer;
        menuOpen = true;
    }

    void BuildContextMenu(FlyoutBuilder menu)
    {
        var node = menuNode;
        var isRoot = node is not null && node.Parent is null;

        menu.Item("Rename", () => BeginRename(node), enabled: node is not null && !isRoot);
        menu.Item("Delete", DeleteSelected, enabled: HasEditableSelection);
        menu.Item("Duplicate", DuplicateSelected, enabled: HasEditableSelection);
        menu.Item("Move Up", () => Restructure(node!, () => MoveBy(node!, -1)),
            enabled: node?.Parent is { } above && above.Children.IndexOf(node) > 0);
        menu.Item("Move Down", () => Restructure(node!, () => MoveBy(node!, 1)),
            enabled: node?.Parent is { } below && below.Children.IndexOf(node) < below.Children.Count - 1);
        menu.Separator();
        menu.Item("Copy", () => clipboard = node, enabled: node is not null);
        menu.Item("Paste", () => Paste(node), enabled: clipboard is not null && node is not null);
        menu.Separator();
        menu.Item("New Node", () => Create(node, asChild: false), enabled: node is not null);
        menu.Item("New Child Node", () => Create(node, asChild: true), enabled: node is not null);
        menu.Separator();
        BuildPrefabMenu(menu, node, isRoot);
    }

    void BuildPrefabMenu(FlyoutBuilder menu, Node? node, bool isRoot)
    {
        menu.Item("Create Prefab", () => CreatePrefab(node), enabled: node is not null && !isRoot);
        menu.Item("Open Prefab", () => prefabStage.OpenPrefab(node!), enabled: node?.PrefabInstance is not null);
        menu.Item("Unpack Prefab", () => Unpack(node, completely: false), enabled: node?.PrefabInstance is not null);
        menu.Item("Unpack Prefab Completely", () => Unpack(node, completely: true),
            enabled: node?.PrefabInstance is not null);
    }

    /// <summary>Whether a node other than the scene root is selected, so an edit has a target.</summary>
    public bool HasEditableSelection => inspector.SelectedNodes.Any(node => node.Parent is not null);

    /// <summary>Removes the selected node. What the panel's Delete shortcut runs.</summary>
    public void DeleteSelected() => RestructureSelection(() =>
    {
        operations.Delete(inspector.SelectedNodes);
        Invalidate();
    });

    /// <summary>Starts the in-place rename of the selected node. What the panel's F2 shortcut runs.</summary>
    public void RenameSelected() => BeginRename(inspector.SelectedNode);

    /// <summary>Clones the selected node beside itself. What the panel's Duplicate shortcut runs.</summary>
    public void DuplicateSelected()
    {
        var clones = operations.Duplicate(inspector.SelectedNodes);
        if (clones.Count == 0) return;
        Invalidate();
        inspector.SelectMany(clones);
    }

    void BeginRename(Node? node)
    {
        if (node is null || keysByNode.GetValueOrDefault(node) is not { } key) return;

        state.BeginRename(key, node.Name);
    }

    void Rename(TreeItem item, string name)
    {
        if (item.Tag is not Node node || string.IsNullOrWhiteSpace(name)) return;

        undo.RecordObject(node, "Rename");
        sceneTree.RenameNode(node, name, SiblingNames(node.Parent, except: node));
        sceneTree.MarkAssetModified();
        sceneTree.RefreshNode(node);
        Invalidate();
    }

    void Unpack(Node? node, bool completely)
    {
        if (node is null) return;

        overrides.Unpack(node, completely);
        Invalidate();
    }

    /// <summary>Deletes a node, asking first to unpack the prefab instance that provides it.</summary>
    void Delete(Node? node)
    {
        if (node is null || node.Parent is null) return;

        Restructure(node, () => DeleteNow(node));
    }

    /// <summary>
    /// Runs a change to where <paramref name="node"/> sits in the hierarchy. A node its prefab provides must stay where
    /// the prefab puts it, as in Unity, so the instance is unpacked first when the user agrees.
    /// </summary>
    void Restructure(Node node, Action change)
    {
        if (PrefabOwned(node) is not { } instance)
        {
            change();
            return;
        }

        confirm.Ask(localization.T("Unpack Prefab"),
            string.Format(CultureInfo.CurrentCulture,
                localization.T("\"{0}\" is part of the prefab instance \"{1}\". Unpack the instance to change it?"),
                node.Name, instance.Name),
            localization.T("Unpack and Continue"),
            () =>
            {
                // One step: undo repacks the instance and reverts the change together. Each instance level above the
                // node is unpacked, so a nested prefab's object becomes plain too.
                undo.BeginGesture();
                try
                {
                    while (PrefabOwned(node) is { } owner) overrides.Unpack(owner, completely: false);
                    change();
                }
                finally
                {
                    undo.EndGesture();
                }
            });
    }

    void Reparent(Node node, Node target)
    {
        if (ReferenceEquals(node, target) || node.Parent is not { } parent) return;
        for (var ancestor = target; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, node)) return;

        undo.RecordObject(parent, "Reparent");
        undo.RecordObject(target, "Reparent");
        sceneTree.DetachNode(node);
        sceneTree.AttachNode(node, target, target.Children.Count);
        sceneTree.MarkAssetModified();
        Invalidate();
    }

    void MoveBy(Node node, int offset)
    {
        if (node.Parent is not { } parent) return;

        var index = parent.Children.IndexOf(node);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= parent.Children.Count) return;

        undo.RecordObject(parent, offset < 0 ? "Move Up" : "Move Down");
        parent.Children.Move(index, destination);
        sceneTree.MarkAssetModified();
        Invalidate();
    }

    /// <summary>The instance whose prefab provides <paramref name="node"/>, or null when the node is its own.</summary>
    Node? PrefabOwned(Node node) =>
        PrefabOverrideOperations.InstanceOf(node) is { } instance && !ReferenceEquals(instance, node)
        && overrides.DiffFor(node)?.Added.Contains(node.Id) != true
            ? instance
            : null;

    void DeleteNow(Node node)
    {
        if (node.Parent is not { } parent) return;
        if (ReferenceEquals(inspector.SelectedNode, node)) inspector.ClearSelection();

        undo.RecordObject(parent, "Delete");
        sceneTree.DetachNode(node);
        sceneTree.MarkAssetModified();
        Invalidate();
    }

    /// <summary>Saves the node as a prefab beside the open scene; the node becomes an instance of it.</summary>
    void CreatePrefab(Node? node)
    {
        if (node?.Parent is null || sceneTree.CurrentAsset is not { } scene || settings.Settings is not { } project)
            return;

        var directory = Path.GetDirectoryName(Path.Combine(project.ProjectAbsoluteDir, scene.RelativePath))!;
        if (prefabs.CreatePrefab(node, directory) is null)
        {
            Log.Logger.LogWarning("Could not save {Node} as a prefab", node.Name);
            return;
        }

        sceneTree.MarkAssetModified();
        sceneTree.RefreshNode(node);
        Invalidate();
    }

    void Paste(Node? target)
    {
        if (clipboard is null || target is null) return;

        var parent = target.Parent ?? target;
        var clone = sceneTree.CloneNode(clipboard, NodeName(clipboard.Name, parent));

        undo.RecordObject(parent, "Paste");
        sceneTree.AttachNode(clone, parent, parent.Children.Count);
        sceneTree.MarkAssetModified();
        Invalidate();
    }

    void Create(Node? reference, bool asChild)
    {
        if (reference is null) return;

        var parent = asChild ? reference : reference.Parent ?? reference;
        var node = sceneTree.CreateNode(SiblingNames(parent));

        undo.RecordObject(parent, "Create Node");
        sceneTree.AttachNode(node, parent, parent.Children.Count);
        sceneTree.MarkAssetModified();
        Invalidate();

        if (asChild && keysByNode.GetValueOrDefault(parent) is { } key) state.SetExpanded(key, true);

        Select(node);
    }

    string NodeName(string requested, Node parent) =>
        NodeNaming.GetNextAvailable(requested, SiblingNames(parent));

    static IEnumerable<string> SiblingNames(Node? parent, Node? except = null) =>
        parent is null ? [] : parent.Children.Where(child => !ReferenceEquals(child, except)).Select(child => child.Name);

    void Select(Node node)
    {
        syncedSelection = node;
        sceneTree.SelectNode(node);
        inspector.Select(node);
    }

    NodeSelectionOperations operations => selectionOperations ??= new(sceneTree, inspector, undo);
    NodeSelectionOperations? selectionOperations;

    void PullSelection()
    {
        var nodes = state.SelectedIds.Select(key => nodesByKey.GetValueOrDefault(key)).OfType<Node>().ToArray();
        var active = state.SelectedId is { } key ? nodesByKey.GetValueOrDefault(key) : null;
        if (nodes.SequenceEqual(syncedNodes) && ReferenceEquals(active, syncedSelection)) return;
        inspector.SelectMany(nodes, active);
        syncedNodes = inspector.SelectedNodes;
        syncedSelection = inspector.SelectedNode;
        state.SetSelection(syncedNodes.Select(node => keysByNode.GetValueOrDefault(node)).OfType<string>(),
            syncedSelection is null ? null : keysByNode.GetValueOrDefault(syncedSelection));
        if (syncedSelection is not null) sceneTree.SelectNode(syncedSelection);
    }

    void ReparentSelection(Node dragged, Node target)
    {
        if (!inspector.SelectedNodes.Contains(dragged)) Select(dragged);
        RestructureSelection(() =>
        {
            operations.Reparent(inspector.SelectedNodes, target);
            Invalidate();
        });
    }

    void RestructureSelection(Action change)
    {
        var nodes = SelectionService.TopLevelNodes(inspector.SelectedNodes).ToArray();
        var protectedNode = nodes.FirstOrDefault(node => PrefabOwned(node) is not null);
        if (protectedNode is null) { change(); return; }
        var instance = PrefabOwned(protectedNode)!;
        confirm.Ask(localization.T("Unpack Prefab"),
            string.Format(CultureInfo.CurrentCulture,
                localization.T("\"{0}\" is part of the prefab instance \"{1}\". Unpack the instance to change it?"),
                protectedNode.Name, instance.Name), localization.T("Unpack and Continue"), () =>
            {
                undo.BeginGesture();
                try
                {
                    foreach (var node in nodes)
                        while (PrefabOwned(node) is { } owner) overrides.Unpack(owner, completely: false);
                    change();
                }
                finally { undo.EndGesture(); }
            });
    }

    /// <summary>Imported hierarchies routinely carry unnamed nodes; the type keeps the row readable.</summary>
    /// <summary>Prefab instance roots get an icon: plain, nested, variant, or a broken link when the prefab is gone.</summary>
    static Action<Gui>? PrefabIcon(PrefabLink link)
    {
        var theme = StudioTheme.Current;
        var (glyph, color) = link switch
        {
            PrefabLink.Instance => (EditorIcons.Cube, theme.Accent),
            PrefabLink.NestedInstance => (EditorIcons.Cube, theme.InkDim),
            PrefabLink.VariantInstance => (EditorIcons.Clone, theme.Accent),
            PrefabLink.Missing => (EditorIcons.LinkSlash, theme.Error),
            _ => (null, default),
        };

        return glyph is null ? null : gui => gui.DrawText(glyph, theme.Text(11), color);
    }

    /// <summary>Inactive nodes are faint; nodes a prefab provides take the accent, like Unity's blue prefab rows.</summary>
    static GuiColor? Tint(Node node, PrefabLink link)
    {
        var theme = StudioTheme.Current;
        if (!node.IsActive) return theme.InkFaint;
        return link switch
        {
            PrefabLink.None => null,
            PrefabLink.Missing => theme.Error,
            _ => theme.Accent,
        };
    }

    static string DisplayName(Node node) =>
        string.IsNullOrWhiteSpace(node.Name) ? $"({node.GetType().Name})" : node.Name;
}
