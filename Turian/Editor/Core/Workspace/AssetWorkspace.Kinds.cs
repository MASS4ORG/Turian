namespace Turian.Editor.Core;

/// <summary>How a contributed tab was opened.</summary>
/// <param name="Kind">The kind instance that opened it.</param>
/// <param name="Path">Absolute path of the source.</param>
/// <param name="AssetId">The source's asset id.</param>
/// <param name="ReadOnly">Whether the caller asked for a read-only document.</param>
sealed record ContributedOrigin(IWorkspaceDocumentKind Kind, string Path, Guid AssetId, bool ReadOnly);

/// <summary>
/// What happens to open documents when a document kind comes and goes. A clean document of a removed kind closes and
/// opens again if a kind with the same id returns; a dirty one stays on the old instance, so it can still be saved.
/// </summary>
public sealed partial class AssetWorkspace
{
    // Only ids and paths are kept, so a withdrawn tab holds nothing from the assembly that is leaving.
    sealed record WithdrawnTab(string KindId, string Path, Guid AssetId, bool ReadOnly);

    readonly List<WithdrawnTab> withdrawn = [];

    // The tab shown instead of the withdrawn active one; while it stays in front, the returning tab takes over again.
    AssetWrapper? standIn;
    Guid? toReactivate;

    void OnKindRemoved(IWorkspaceDocumentKind kind)
    {
        var active = Active;
        var wasStandIn = ReferenceEquals(active, standIn);
        foreach (var tab in documents.Where(tab => ReferenceEquals(tab.Origin?.Kind, kind)).ToList())
        {
            if (tab.IsDirty)
            {
                Report($"{kind.Title} is no longer available. {tab.Title} stays open with its unsaved changes; "
                       + "save or close it.");
                continue;
            }

            withdrawn.Add(new WithdrawnTab(kind.Id, tab.Origin!.Path, tab.Origin.AssetId, tab.Origin.ReadOnly));
            Close(tab);
        }

        if (active is null || documents.Contains(active))
        {
            return;
        }

        if (!wasStandIn)
        {
            toReactivate = active.Origin?.AssetId;
        }

        standIn = Active;
    }

    void OnKindAdded(IWorkspaceDocumentKind kind)
    {
        foreach (var tab in withdrawn.Where(tab => tab.KindId == kind.Id).ToList())
        {
            withdrawn.Remove(tab);
            Restore(kind, tab);
        }
    }

    // A source that is open again, gone, or no longer claimed stays as it is.
    bool CanReturn(IWorkspaceDocumentKind kind, WithdrawnTab tab) =>
        Find(tab.AssetId.ToString()) is null && File.Exists(tab.Path) && Claims(kind, tab.Path);

    void Restore(IWorkspaceDocumentKind kind, WithdrawnTab tab)
    {
        if (!CanReturn(kind, tab))
        {
            return;
        }

        var current = Active;
        var takesOver = tab.AssetId == toReactivate && ReferenceEquals(current, standIn);
        if (OpenContributed(kind, tab.Path, tab.AssetId, tab.ReadOnly) is not { } reopened)
        {
            return;
        }

        if (takesOver)
        {
            toReactivate = null;
        }
        else if (current is null)
        {
            standIn = reopened;
        }
        else
        {
            Activate(current);
        }
    }

    void Forget(AssetWrapper closed)
    {
        if (ReferenceEquals(standIn, closed))
        {
            standIn = null;
        }
    }

    void ForgetWithdrawn()
    {
        withdrawn.Clear();
        standIn = null;
        toReactivate = null;
    }
}
