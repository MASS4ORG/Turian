namespace Gaya.Plugin.Turian;

/// <summary>
/// Remembers which scene tab started the running play session, so that tab's Game panel shows the game while the Game
/// panels of the other tabs say where it is playing. The binding survives tab switches; it only moves when the playing
/// tab itself is closed, to whichever tab becomes active, so the game always has a panel to show it in.
/// </summary>
sealed class PlayingDocument : IDisposable
{
    readonly PlayModeService playMode;
    readonly IDocumentService documents;
    string? id;

    /// <summary>Starts following play sessions.</summary>
    /// <param name="playMode">The editor's play session.</param>
    /// <param name="documents">The workbench's documents.</param>
    public PlayingDocument(PlayModeService playMode, IDocumentService documents)
    {
        this.playMode = playMode;
        this.documents = documents;
        playMode.Starting += OnStarting;
        documents.ActiveChanged += OnActiveChanged;
        documents.Closed += OnClosed;
    }

    /// <summary>The title of the tab the session plays in, or null when nothing is playing there.</summary>
    public string? Title => playMode.IsActive ? documents.Documents.FirstOrDefault(open => open.Id == id)?.Title : null;

    /// <summary>Whether a session is running in a tab other than <paramref name="documentId"/>.</summary>
    /// <param name="documentId">The document to ask about.</param>
    public bool IsPlayingElsewhere(string documentId) => playMode.IsActive && id is not null && id != documentId;

    /// <summary>Whether a session is running in the tab <paramref name="documentId"/>.</summary>
    /// <param name="documentId">The document to ask about.</param>
    public bool IsPlayingIn(string documentId) => playMode.IsActive && id == documentId;

    void OnStarting() => id = documents.Active?.Id;

    // Keys held by the game would otherwise stay down while its panel is out of sight.
    void OnActiveChanged(IDocument? document)
    {
        if (playMode.IsActive && id is not null && document?.Id != id)
        {
            playMode.Input.Clear();
        }
    }

    void OnClosed(IDocument document)
    {
        if (id == document.Id)
        {
            id = documents.Active?.Id;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        playMode.Starting -= OnStarting;
        documents.ActiveChanged -= OnActiveChanged;
        documents.Closed -= OnClosed;
    }
}
