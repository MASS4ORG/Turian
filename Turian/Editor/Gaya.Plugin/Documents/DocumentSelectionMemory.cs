namespace Gaya.Plugin.Turian;

/// <summary>
/// Gives each scene tab its own node selection on top of the one global inspector selection: leaving a tab remembers
/// which nodes were selected there, and activating a tab selects its remembered nodes again, so the Inspector always
/// shows the active tab's selection. Nodes are remembered by id and looked up in the tab's own hierarchy, so a rebuilt
/// scene keeps its selection.
/// </summary>
sealed class DocumentSelectionMemory : IDisposable
{
    readonly IDocumentService documents;
    readonly NodeInspectorController inspector;
    readonly SceneTreeController sceneTree;
    readonly Dictionary<string, Remembered> remembered = [];
    string? current;

    sealed record Remembered(IReadOnlyList<Guid> Nodes, Guid? Active);

    /// <summary>Starts following which document is active.</summary>
    /// <param name="documents">The workbench's documents.</param>
    /// <param name="inspector">The global selection.</param>
    /// <param name="sceneTree">Where each open scene's hierarchy is found.</param>
    public DocumentSelectionMemory(IDocumentService documents, NodeInspectorController inspector,
        SceneTreeController sceneTree)
    {
        this.documents = documents;
        this.inspector = inspector;
        this.sceneTree = sceneTree;
        current = documents.Active?.Id;
        documents.ActiveChanged += OnActiveChanged;
        documents.Closed += OnClosed;
    }

    void OnActiveChanged(IDocument? document)
    {
        if (current is { } previous)
        {
            // A document that closed took its selection with it; the service has already dropped it from the open list.
            if (documents.Documents.Any(open => open.Id == previous))
            {
                remembered[previous] = new Remembered(
                    [.. inspector.SelectedNodes.Select(static node => node.Id)], inspector.SelectedNode?.Id);
            }
            else
            {
                remembered.Remove(previous);
            }
        }

        current = document?.Id;
        Restore(document as SceneDocument);
    }

    void OnClosed(IDocument document) => remembered.Remove(document.Id);

    /// <summary>
    /// Selects the nodes remembered for the tab. A tab with nothing remembered clears node selections left over from the
    /// previous tab, which belong to another hierarchy, and leaves any other selection (such as an asset) alone.
    /// </summary>
    void Restore(SceneDocument? document)
    {
        var (nodes, active) = Resolve(document);
        if (nodes.Count > 0)
        {
            inspector.SelectMany(nodes, active);
        }
        else if (inspector.SelectedNodes.Count > 0)
        {
            inspector.ClearSelection();
        }
    }

    (List<Node> Nodes, Node? Active) Resolve(SceneDocument? document)
    {
        var nodes = new List<Node>();
        if (document is null || !remembered.TryGetValue(document.Id, out var memory)
            || RootOf(document) is not { } root)
        {
            return (nodes, null);
        }

        nodes.AddRange(memory.Nodes.Select(id => SceneTreeController.FindNodeById(root, id)).OfType<Node>());
        return (nodes, nodes.Find(node => node.Id == memory.Active));
    }

    /// <summary>The hierarchy selected nodes live in: the running copy while Play shows one, else the tab's scene.</summary>
    Node? RootOf(SceneDocument document) =>
        sceneTree.IsShowingRuntimeScene ? sceneTree.CurrentSceneRoot : sceneTree.FindSceneRoot(document.AssetId);

    /// <inheritdoc />
    public void Dispose()
    {
        documents.ActiveChanged -= OnActiveChanged;
        documents.Closed -= OnClosed;
    }
}
