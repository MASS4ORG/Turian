using System.Text.Json.Nodes;

namespace Turian.Editor.Core;

/// <summary>
/// Undo and redo for the whole project, as in Unity: one history whose steps each belong to a document — an open
/// scene, or an asset whose content the inspector edits. The selected node and its components are watched, any other
/// object is recorded with <see cref="RecordObject"/> before it changes, and <see cref="Flush"/> turns what changed
/// since the last call into one step.
/// </summary>
/// <remarks>
/// Undoing a step of another scene brings that scene to the front first. When a scene is rebuilt from its saved form,
/// its steps move to the rebuilt objects. Nothing is recorded while Play Mode shows the running scene.
/// </remarks>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class UndoService : IDisposable
{
    readonly SceneTreeController sceneTree;
    readonly NodeInspectorController inspector;
    readonly AssetManager assets;
    readonly UndoHistory history = new();
    readonly Dictionary<Guid, AssetInspection> inspectedAssets = [];
    readonly Dictionary<Guid, Guid> savedAt = [];
    readonly Dictionary<IdClass, ObjectState> watched = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<IdClass> recorded = new(ReferenceEqualityComparer.Instance);
    string? recordedLabel;
    bool altered;
    bool restoring;
    int gestures;
    Guid gestureStep;

    /// <summary>Follows the selection, the edits and the documents.</summary>
    /// <param name="sceneTree">The open scenes.</param>
    /// <param name="inspector">The selection to watch.</param>
    /// <param name="assets">Reports edits and closed documents, and brings a document to the front.</param>
    public UndoService(SceneTreeController sceneTree, NodeInspectorController inspector, AssetManager assets)
    {
        this.sceneTree = sceneTree;
        this.inspector = inspector;
        this.assets = assets;

        inspector.SelectionChanged += OnSelectionChanged;
        sceneTree.SceneLoaded += OnSceneLoaded;
        sceneTree.SceneRebuilt += OnSceneRebuilt;
        assets.AssetAltered += OnAssetAltered;
        assets.AssetClosed += OnAssetClosed;
        assets.AssetSaved += OnAssetSaved;
    }

    /// <summary>The document of changes that belong to the whole project, such as moving files.</summary>
    public static Guid ProjectDocument => Guid.Empty;

    /// <summary>Raised after a step is recorded, undone or redone.</summary>
    public event Action? Changed;

    /// <summary>Raised after an undo or redo changed an asset's content, so it can be saved.</summary>
    public event Action<AssetInspection>? AssetRestored;

    /// <summary>The project's history.</summary>
    public UndoHistory History => history;

    /// <summary>Whether there is a step to undo; never while Play Mode shows the running scene.</summary>
    public bool CanUndo => !sceneTree.IsShowingRuntimeScene && history.CanUndo;

    /// <summary>Whether there is a step to redo; never while Play Mode shows the running scene.</summary>
    public bool CanRedo => !sceneTree.IsShowingRuntimeScene && history.CanRedo;

    /// <summary>The label of the step Undo would revert, or null.</summary>
    public string? UndoLabel => CanUndo ? history.UndoSteps[^1].Label : null;

    /// <summary>The label of the step Redo would reapply, or null.</summary>
    public string? RedoLabel => CanRedo ? history.RedoSteps[^1].Label : null;

    AssetInspection? InspectedContent =>
        inspector.SelectedObject is AssetInspection { IsPayload: true, Target: IdClass } inspection ? inspection : null;

    // The document an edit made now belongs to: the inspected asset's content, else the open scene.
    Guid? CurrentDocument =>
        sceneTree.IsShowingRuntimeScene ? null : InspectedContent?.Metadata.Id ?? sceneTree.CurrentAsset?.Id;

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

    /// <summary>
    /// Performs a change that reaches beyond scene objects, such as rewriting a prefab file, as one undoable step.
    /// Nothing is recorded when the change throws.
    /// </summary>
    /// <param name="label">What the change is.</param>
    /// <param name="objects">
    /// Scene objects the change also affects: their states before and after it are restored with the step. With
    /// <paramref name="keepValues"/>, they instead keep the values they have now, whether the step is undone or redone.
    /// </param>
    /// <param name="perform">Makes the change; it also runs again for redo.</param>
    /// <param name="revert">Undoes the change.</param>
    /// <param name="keepValues">Whether <paramref name="objects"/> keep their current values.</param>
    /// <param name="document">The document the step belongs to; the current one when null.</param>
    public void Perform(string label, IEnumerable<IdClass> objects, Action perform, Action revert,
        bool keepValues = false, Guid? document = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(perform);
        ArgumentNullException.ThrowIfNull(revert);

        Flush();
        var owner = document ?? CurrentDocument ?? ProjectDocument;
        var before = Capture(objects);

        if (keepValues)
        {
            // Pushed before it runs, so a rebuild the change causes moves the step to the rebuilt objects too.
            history.Push(new UndoStep(label, owner, before, before) { UndoEffect = revert, RedoEffect = perform },
                mergeable: false);
            try
            {
                Run(perform);
            }
            catch
            {
                history.CancelLatest();
                throw;
            }
        }
        else
        {
            Run(perform);
            history.Push(new UndoStep(label, owner, before, Capture(before.Keys))
            { UndoEffect = revert, RedoEffect = perform }, mergeable: false);
        }

        WatchSelection();
        Changed?.Invoke();
    }

    /// <summary>Starts a gesture, such as a gizmo drag: everything it changes, however long, is one step.</summary>
    public void BeginGesture()
    {
        if (gestures++ > 0) return;

        Flush();
        gestureStep = Guid.Empty;
    }

    /// <summary>Ends the gesture <see cref="BeginGesture"/> started.</summary>
    public void EndGesture()
    {
        if (gestures == 0) return;

        Flush();
        if (--gestures == 0) gestureStep = Guid.Empty;
    }

    static Dictionary<IdClass, ObjectState> Capture(IEnumerable<IdClass> objects)
    {
        var states = new Dictionary<IdClass, ObjectState>(ReferenceEqualityComparer.Instance);
        foreach (var target in objects) states[target] = ObjectState.Capture(target);
        return states;
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

        if (CurrentDocument is not { } document || restoring)
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

        if (InspectedContent is { } inspection) inspectedAssets[document] = inspection;
        var step = new UndoStep(label ?? EditLabel(before.Keys), document, before, after);
        if (gestures > 0 && gestureStep != Guid.Empty
            && history.UndoSteps is [.., { } latest] && latest.Id == gestureStep)
        {
            history.MergeIntoLatest(step);
        }
        else
        {
            history.Push(step, mergeable: !explicitStep && gestures == 0);
            if (gestures > 0) gestureStep = history.UndoSteps[^1].Id;
        }

        Changed?.Invoke();
    }

    /// <summary>Reverts the latest step, bringing its document to the front first.</summary>
    public void Undo()
    {
        Flush();
        if (!CanUndo) return;

        Show(history.UndoSteps[^1].Document);
        Finish(RunEffect(history.Undo, "undo"));
    }

    /// <summary>Reapplies the latest undone step, bringing its document to the front first.</summary>
    public void Redo()
    {
        Flush();
        if (!CanRedo) return;

        Show(history.RedoSteps[^1].Document);
        Finish(RunEffect(history.Redo, "redo"));
    }

    // A step whose effect failed, such as a file that could not be moved back, stays in place to be tried again.
    UndoStep? RunEffect(Func<UndoStep?> action, string what)
    {
        try
        {
            return Run(action);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidOperationException)
        {
            Log.Logger.LogError(exception, "Could not {Action} the last change", what);
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        inspector.SelectionChanged -= OnSelectionChanged;
        sceneTree.SceneLoaded -= OnSceneLoaded;
        sceneTree.SceneRebuilt -= OnSceneRebuilt;
        assets.AssetAltered -= OnAssetAltered;
        assets.AssetClosed -= OnAssetClosed;
        assets.AssetSaved -= OnAssetSaved;
    }

    void OnAssetSaved(Asset asset)
    {
        Flush();
        savedAt[asset.Id] = history.LatestFor(asset.Id);
        history.Seal(asset.Id);
    }

    // An asset step shows the asset in the inspector; a scene step makes its scene the open one.
    void Show(Guid document)
    {
        if (inspectedAssets.TryGetValue(document, out var inspection))
        {
            if (!ReferenceEquals(inspector.SelectedObject, inspection)) inspector.Select(inspection);
            return;
        }

        if (sceneTree.CurrentAsset?.Id != document && assets.GetTrackedAsset(document) is { } scene)
            assets.ActivateAsset(scene);
    }

    T Run<T>(Func<T> action)
    {
        restoring = true;
        try
        {
            return action();
        }
        finally
        {
            restoring = false;
        }
    }

    void Run(Action action) => Run(() =>
    {
        action();
        return true;
    });

    void Finish(UndoStep? step)
    {
        if (step is null) return;

        if (inspectedAssets.TryGetValue(step.Document, out var inspection)) AssetRestored?.Invoke(inspection);
        else if (step.Document != ProjectDocument) RefreshScene(step);

        altered = false;
        WatchSelection();
        Changed?.Invoke();
    }

    void RefreshScene(UndoStep step)
    {
        restoring = true;
        try
        {
            sceneTree.MarkAssetModified();
            foreach (var node in step.Before.Keys.OfType<Node>()) assets.RefreshNode(node);
            assets.UpdateSelectedNode();

            // A step can take the selected node out of the scene, such as undoing its creation.
            if (inspector.SelectedNode is { } selected && !InScene(selected)) inspector.ClearSelection();
            else inspector.Select(inspector.SelectedObject);

            // Back at the step the scene was saved at, its content is what is on disk again.
            if (savedAt.TryGetValue(step.Document, out var saved) && saved == history.LatestFor(step.Document))
                assets.MarkClean(step.Document);
        }
        finally
        {
            restoring = false;
        }
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

    void OnAssetClosed(Asset asset)
    {
        history.Remove(asset.Id);
        inspectedAssets.Remove(asset.Id);
        savedAt.Remove(asset.Id);
        Changed?.Invoke();
    }

    // The scene's steps move to the rebuilt objects, matched by id. An object only the history still holds, such as a
    // deleted node, is rebuilt from its saved form too, so it has the current types after a recompile.
    void OnSceneRebuilt(Guid assetId, Node oldRoot, Node newRoot)
    {
        var replacements = new Dictionary<Guid, IdClass>();
        Register(newRoot);
        history.Remap(assetId, Replace);
        Changed?.Invoke();

        IdClass Replace(IdClass old)
        {
            if (old is not (Node or Component)) return old;
            if (replacements.TryGetValue(old.Id, out var found)) return found;

            IdClass? rebuilt = old switch
            {
                Node node => NodeCloner.DeepClone(node, awake: false),
                Component component => RebuildComponent(component),
                _ => null,
            };
            if (rebuilt is null) return old;

            Register(rebuilt);
            return replacements.GetValueOrDefault(old.Id, rebuilt);
        }

        void Register(IdClass obj)
        {
            replacements.TryAdd(obj.Id, obj);
            if (obj is not Node node) return;

            foreach (var component in node.Components) replacements.TryAdd(component.Id, component);
            foreach (var child in node.Children) Register(child);
        }
    }

    // A component is read the way a scene reads it, inside a node, then taken out of that node.
    static Component? RebuildComponent(Component component)
    {
        var holder = new JsonObject
        {
            [ObjectJsonSerializer<IdClass>.TypeIdProperty] = TypeRegistry.GetIdOrThrow(typeof(Node)).ToString(),
            [nameof(Node.Components)] = new JsonArray(JsonNode.Parse(Serializer.Serialize(component))),
        };

        if (Serializer.LoadData<Node>(holder.ToJsonString()) is not { Components.Count: > 0 } node) return null;

        var rebuilt = node.Components[0];
        node.Components.Clear();
        return rebuilt;
    }

    static string EditLabel(IEnumerable<IdClass> changed) => changed.FirstOrDefault() switch
    {
        Node node => $"Edit {node.Name}",
        Component component => $"Edit {component.GetType().Name}",
        _ => "Edit",
    };
}
