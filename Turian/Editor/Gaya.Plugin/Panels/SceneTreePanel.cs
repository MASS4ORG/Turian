namespace Gaya.Plugin.Turian;

/// <summary>
/// The scene hierarchy: the open scene's nodes, expandable, with the selected one driving the
/// inspector. A right-click offers rename, delete, copy, paste and node creation.
/// </summary>
sealed class SceneTreePanel : IPanel
{

    readonly TreeViewState state = new();
    readonly List<TreeItem> rows = [];
    readonly Dictionary<Node, string> keysByNode = [];
    readonly Dictionary<string, Node> nodesByKey = [];

    Node? syncedSelection;

    readonly SceneTreeController sceneTree;
    readonly NodeInspectorController inspector;

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
    public SceneTreePanel(SceneTreeController sceneTree, NodeInspectorController inspector, AssetManager assets)
    {
        this.sceneTree = sceneTree;
        this.inspector = inspector;

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
        if (gui.Pass == Pass.Pass1Build && (stale || !ReferenceEquals(builtFor, root))) Rebuild(root);

        // The selection flows both ways: the controller wins when something else changed it, and the
        // tree wins when the keyboard moved it. Pushing the controller's value in unconditionally
        // undid every arrow-key move on the next frame.
        if (!ReferenceEquals(inspector.SelectedNode, syncedSelection))
        {
            syncedSelection = inspector.SelectedNode;
            state.SelectedId = syncedSelection is null ? null : keysByNode.GetValueOrDefault(syncedSelection);
            if (state.SelectedId is { } revealed) Reveal(revealed);
        }

        gui.TreeView(state, rows, StudioControls.Tree(), OnClick, DragPayload, Rename, OnEmptyClick,
            dropAccept: payload => payload is ScriptDragPayload drop
                && typeof(Component).IsAssignableFrom(drop.ComponentType),
            onDrop: (item, payload) =>
            {
                if (item.Tag is Node node && payload is ScriptDragPayload drop)
                {
                    inspector.Select(node);
                    if (inspector.AddComponent(drop.ComponentType)) Invalidate();
                }
            });

        if (gui.Pass == Pass.Pass2Render
            && state.SelectedId is { } selectedKey
            && nodesByKey.GetValueOrDefault(selectedKey) is { } moved
            && !ReferenceEquals(moved, syncedSelection))
            Select(moved);

        gui.CascadeMenu(ref menuOpen, menuAt, BuildContextMenu);
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
        Append(root, depth: 0, key: "0");

        builtFor = root;
        stale = false;
    }

    /// <summary>
    /// Keys are the path of child indices from the root. Node ids cannot be used: imported scenes have
    /// been seen carrying the same id on several nodes, which would collapse them into a single row.
    /// </summary>
    void Append(Node node, int depth, string key)
    {
        keysByNode[node] = key;
        nodesByKey[key] = node;
        rows.Add(new TreeItem(
            key,
            DisplayName(node),
            depth,
            node.Children.Count > 0,
            Tag: node,
            Tint: node.IsActive ? null : StudioTheme.Current.InkFaint));

        var children = node.Children.ToList();
        for (var i = 0; i < children.Count; i++) Append(children[i], depth + 1, $"{key}/{i}");
    }

    void OnClick(TreeViewEvent e)
    {
        if (e.Item.Tag is not Node node) return;

        if (e.Button == MouseButton.Right)
        {
            menuNode = node;
            menuAt = pointer;
            menuOpen = true;
            return;
        }

        if (e.Button != MouseButton.Left) return;

        Select(node);
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
        menu.Item("Delete", () => Delete(node), enabled: node is not null && !isRoot);
        menu.Separator();
        menu.Item("Copy", () => clipboard = node, enabled: node is not null);
        menu.Item("Paste", () => Paste(node), enabled: clipboard is not null && node is not null);
        menu.Separator();
        menu.Item("New Node", () => Create(node, asChild: false), enabled: node is not null);
        menu.Item("New Child Node", () => Create(node, asChild: true), enabled: node is not null);
    }

    /// <summary>Whether a node other than the scene root is selected, so an edit has a target.</summary>
    public bool HasEditableSelection => inspector.SelectedNode is { Parent: not null };

    /// <summary>Removes the selected node. What the panel's Delete shortcut runs.</summary>
    public void DeleteSelected() => Delete(inspector.SelectedNode);

    /// <summary>Starts the in-place rename of the selected node. What the panel's F2 shortcut runs.</summary>
    public void RenameSelected() => BeginRename(inspector.SelectedNode);

    /// <summary>Clones the selected node beside itself. What the panel's Duplicate shortcut runs.</summary>
    public void DuplicateSelected()
    {
        if (inspector.SelectedNode is not { Parent: { } parent } node) return;

        var clone = sceneTree.CloneNode(node, NodeName(node.Name, parent));
        sceneTree.AttachNode(clone, parent, parent.Children.Count);
        sceneTree.MarkAssetModified();
        Invalidate();
        Select(clone);
    }

    void BeginRename(Node? node)
    {
        if (node is null || keysByNode.GetValueOrDefault(node) is not { } key) return;

        state.BeginRename(key, node.Name);
    }

    void Rename(TreeItem item, string name)
    {
        if (item.Tag is not Node node || string.IsNullOrWhiteSpace(name)) return;

        sceneTree.RenameNode(node, name, SiblingNames(node.Parent, except: node));
        sceneTree.MarkAssetModified();
        sceneTree.RefreshNode(node);
        Invalidate();
    }

    void Delete(Node? node)
    {
        if (node is null || node.Parent is null) return;

        if (ReferenceEquals(inspector.SelectedNode, node)) inspector.ClearSelection();

        sceneTree.DetachNode(node);
        sceneTree.MarkAssetModified();
        Invalidate();
    }

    void Paste(Node? target)
    {
        if (clipboard is null || target is null) return;

        var parent = target.Parent ?? target;
        var clone = sceneTree.CloneNode(clipboard, NodeName(clipboard.Name, parent));

        sceneTree.AttachNode(clone, parent, parent.Children.Count);
        sceneTree.MarkAssetModified();
        Invalidate();
    }

    void Create(Node? reference, bool asChild)
    {
        if (reference is null) return;

        var parent = asChild ? reference : reference.Parent ?? reference;
        var node = sceneTree.CreateNode(SiblingNames(parent));

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

    /// <summary>Imported hierarchies routinely carry unnamed nodes; the type keeps the row readable.</summary>
    static string DisplayName(Node node) =>
        string.IsNullOrWhiteSpace(node.Name) ? $"({node.GetType().Name})" : node.Name;
}
