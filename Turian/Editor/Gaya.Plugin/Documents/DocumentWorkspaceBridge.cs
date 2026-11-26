namespace Gaya.Plugin.Turian;

/// <summary>
/// Mirrors the <see cref="AssetWorkspace"/> tabs into Gaya's <see cref="IDocumentService"/>, so the workbench swaps to the
/// scene layout and gives each tab its own panels. The workspace stays the source of truth: opening, activating and
/// closing a tab (including closing every tab on a project switch) is forwarded, and nothing flows back.
/// </summary>
sealed class DocumentWorkspaceBridge : IDisposable
{
    readonly AssetWorkspace workspace;
    readonly IDocumentService documents;

    /// <summary>Starts following the workspace, adopting the tabs it already has.</summary>
    /// <param name="workspace">The open tabs.</param>
    /// <param name="documents">The workbench's documents.</param>
    public DocumentWorkspaceBridge(AssetWorkspace workspace, IDocumentService documents)
    {
        this.workspace = workspace;
        this.documents = documents;

        foreach (var tab in workspace.Documents)
        {
            OnOpened(tab);
        }

        OnActivated(workspace.Active);
        workspace.Opened += OnOpened;
        workspace.Closed += OnClosed;
        workspace.Activated += OnActivated;
    }

    void OnOpened(AssetWrapper tab)
    {
        if (tab.Asset is not null)
        {
            documents.Open(AssetDocument.For(tab));
        }
    }

    void OnClosed(AssetWrapper tab)
    {
        if (tab.Asset is { } asset)
        {
            documents.Close(asset.Id.ToString());
        }
    }

    void OnActivated(AssetWrapper? tab)
    {
        if (tab?.Asset is { } asset)
        {
            documents.Activate(asset.Id.ToString());
        }
        else
        {
            documents.Deactivate();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        workspace.Opened -= OnOpened;
        workspace.Closed -= OnClosed;
        workspace.Activated -= OnActivated;
    }
}
