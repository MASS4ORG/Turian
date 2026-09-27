namespace Turian.Editor.Core;

/// <summary>
/// Undo and redo for the open scenes, one history per document, the way Unity's <c>Undo</c> works: the selected node
/// and its components are watched, and any other object is recorded with <see cref="RecordObject"/> before it is
/// changed. <see cref="Flush"/> turns what changed since the last call into one step.
/// </summary>
/// <remarks>
/// While the inspector shows an asset's own content, such as a data asset, that asset has the active history instead.
/// Steps hold the objects themselves, so a document's history is cleared when its scene is rebuilt from its saved
/// form. Nothing is recorded while Play Mode shows the running scene.
/// </remarks>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class UndoService : IDisposable
{
    readonly SceneTreeController sceneTree;
    readonly NodeInspectorController inspector;
    readonly AssetManager assets;
    readonly Dictionary<Guid, UndoHistory> histories = [];
    readonly Dictionary<IdClass, ObjectState> watched = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<IdClass> recorded = new(ReferenceEqualityComparer.Instance);
    string? recordedLabel;
    bool altered;
    bool restoring;

    /// <summary>Follows the selection, the edits and the documents.</summary>
    /// <param name="sceneTree">The open scenes.</param>
    /// <param name="inspector">The selection to watch.</param>
    /// <param name="assets">Reports edits and closed documents.</param>
    public UndoService(SceneTreeController sceneTree, NodeInspectorController inspector, AssetManager assets)
    {
        this.sceneTree = sceneTree;
        this.inspector = inspector;
        this.assets = assets;

        inspector.SelectionChanged += OnSelectionChanged;
        sceneTree.SceneLoaded += OnSceneLoaded;
        sceneTree.SceneRebuilt += Forget;
        assets.AssetAltered += OnAssetAltered;
        assets.AssetClosed += OnAssetClosed;
    }

    /// <summary>Raised after a step is recorded, undone or redone.</summary>
    public event Action? Changed;

    /// <summary>
    /// The active history: the inspected asset's, else the open scene's. Null when there is neither or Play Mode is
    /// showing.
    /// </summary>
    public UndoHistory? History =>
        sceneTree.IsShowingRuntimeScene ? null
        : InspectedContent is { } inspected ? HistoryFor(inspected.Metadata.Id)
        : sceneTree.CurrentAsset is { } asset ? HistoryFor(asset.Id)
        : null;

    AssetInspection? InspectedContent =>
        inspector.SelectedObject is AssetInspection { IsPayload: true, Target: IdClass } inspection ? inspection : null;

    /// <summary>Whether the active document has a step to undo.</summary>
    public bool CanUndo => History?.CanUndo == true;

    /// <summary>Whether the active document has a step to redo.</summary>
    public bool CanRedo => History?.CanRedo == true;

    /// <summary>The label of the step Undo would revert, or null.</summary>
    public string? UndoLabel => History is { CanUndo: true } history ? history.UndoSteps[^1].Label : null;

    /// <summary>The label of the step Redo would reapply, or null.</summary>
    public string? RedoLabel => History is { CanRedo: true } history ? history.RedoSteps[^1].Label : null;

    /// <summary>
    /// Remembers <paramref name="target"/>'s state before a change, so the next <see cref="Flush"/> records the
    /// change as its own step. Record a node's parent before adding, removing or moving the node.
    /// </summary>
    /// <param name="target">The node or component about to change.</param>
    /// <param name="label">What the change is, such as <c>Delete</c>.</param>
    public void RecordObject(IdClass target, string label)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (altered || recordedLabel != label) Flush();

        // Recorded again within the same step, it keeps the state from before the step's first change.
        if (recorded.Add(target)) watched[target] = ObjectState.Capture(target);
        recordedLabel = label;
    }

    /// <summary>Notes an edit that did not go through the open scene, such as one to an inspected asset.</summary>
    public void MarkAltered()
    {
        if (!restoring) altered = true;
    }

    /// <summary>Records what changed since the last call as one step. The studio calls it once a frame.</summary>
    public void Flush()
    {
        if (!altered && recorded.Count == 0) return;
        altered = false;

        var label = recordedLabel;
        var explicitStep = recorded.Count > 0;
        recordedLabel = null;
        recorded.Clear();

        if (History is not { } history || restoring)
        {
            WatchSelection();
            return;
        }

        var before = new Dictionary<IdClass, ObjectState>(ReferenceEqualityComparer.Instance);
        var after = new Dictionary<IdClass, ObjectState>(ReferenceEqualityComparer.Instance);
        foreach (var (target, previous) in watched)
        {
            var current = ObjectState.Capture(target);
            if (previous.SameAs(current)) continue;

            before[target] = previous;
            after[target] = current;
        }

        WatchSelection();
        if (before.Count == 0) return;

        history.Push(new UndoStep(label ?? EditLabel(before.Keys), before, after), mergeable: !explicitStep);
        Changed?.Invoke();
    }

    /// <summary>Reverts the active document's latest step.</summary>
    public void Undo()
    {
        Flush();
        Apply(History?.Undo());
    }

    /// <summary>Reapplies the active document's latest undone step.</summary>
    public void Redo()
    {
        Flush();
        Apply(History?.Redo());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        inspector.SelectionChanged -= OnSelectionChanged;
        sceneTree.SceneLoaded -= OnSceneLoaded;
        sceneTree.SceneRebuilt -= Forget;
        assets.AssetAltered -= OnAssetAltered;
        assets.AssetClosed -= OnAssetClosed;
    }

    void Apply(UndoStep? step)
    {
        if (step is null) return;

        restoring = true;
        try
        {
            // An asset's content is saved by whoever listens to Changed; a scene is marked and redrawn here.
            if (InspectedContent is null) RefreshScene(step);
        }
        finally
        {
            restoring = false;
            altered = false;
            WatchSelection();
        }

        Changed?.Invoke();
    }

    void RefreshScene(UndoStep step)
    {
        sceneTree.MarkAssetModified();
        foreach (var node in step.Before.Keys.OfType<Node>()) assets.RefreshNode(node);
        assets.UpdateSelectedNode();

        // A step can take the selected node out of the scene, such as undoing its creation.
        if (inspector.SelectedNode is { } selected && !InScene(selected)) inspector.ClearSelection();
        else inspector.Select(inspector.SelectedObject);
    }

    bool InScene(Node node)
    {
        var root = node;
        while (root.Parent is not null) root = root.Parent;
        return ReferenceEquals(root, sceneTree.EditorSceneRoot);
    }

    // Edits to the previous selection are recorded before the new one is watched.
    void OnSelectionChanged()
    {
        Flush();
        WatchSelection();
    }

    // The selected node and its components are watched, so edits made anywhere to them are recorded.
    void WatchSelection()
    {
        watched.Clear();
        if (sceneTree.IsShowingRuntimeScene) return;
        if (InspectedContent?.Target is IdClass content) watched[content] = ObjectState.Capture(content);
        if (inspector.SelectedNode is not { } node) return;

        watched[node] = ObjectState.Capture(node);
        foreach (var component in node.Components) watched[component] = ObjectState.Capture(component);
    }

    void OnSceneLoaded(Node? root)
    {
        altered = false;
        recorded.Clear();
        WatchSelection();
    }

    void OnAssetAltered(Asset asset)
    {
        if (!restoring) altered = true;
    }

    void OnAssetClosed(Asset asset) => histories.Remove(asset.Id);

    void Forget(Guid assetId)
    {
        if (histories.TryGetValue(assetId, out var history)) history.Clear();
        Changed?.Invoke();
    }

    UndoHistory HistoryFor(Guid assetId)
    {
        if (!histories.TryGetValue(assetId, out var history)) histories[assetId] = history = new UndoHistory();
        return history;
    }

    static string EditLabel(IEnumerable<IdClass> changed) => changed.FirstOrDefault() switch
    {
        Node node => $"Edit {node.Name}",
        Component component => $"Edit {component.GetType().Name}",
        _ => "Edit",
    };
}
