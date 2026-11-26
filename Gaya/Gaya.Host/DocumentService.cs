namespace Gaya.Host;

/// <summary>
/// The <see cref="IDocumentService"/> the host registers before plugins are configured. It only tracks which documents
/// are open and which is active; the <see cref="Workbench"/> follows it to swap layouts and panel instances.
/// </summary>
public sealed class DocumentService : IDocumentService
{
    readonly List<IDocument> open = [];
    readonly List<IDocument> recent = [];
    IDocument? active;

    /// <inheritdoc />
    public IDocument? Active => active;

    /// <inheritdoc />
    public IReadOnlyList<IDocument> Documents => open;

    /// <inheritdoc />
    public event Action<IDocument?>? ActiveChanged;

    /// <inheritdoc />
    public event Action<IDocument>? Closed;

    /// <summary>Open documents, most recently active first.</summary>
    internal IReadOnlyList<IDocument> Recent => recent;

    /// <summary>Raised before <see cref="ActiveChanged"/>, so the workbench has switched before any host handler runs.</summary>
    internal event Action<IDocument?>? ActivationSwitched;

    /// <summary>Raised before <see cref="Closed"/>, so the workbench has released the document's panels first.</summary>
    internal event Action<IDocument>? ClosingDocument;

    /// <inheritdoc />
    public void Open(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Kind);
        if (document.Kind == WorkbenchLayoutStore.NoDocumentKey)
        {
            throw new ArgumentException($"'{document.Kind}' is reserved for the no-document layout.", nameof(document));
        }

        var existing = Find(document.Id);
        if (existing is null)
        {
            open.Add(document);
        }

        SetActive(existing ?? document);
    }

    /// <inheritdoc />
    public bool Activate(string documentId)
    {
        if (Find(documentId) is not { } document)
        {
            return false;
        }

        SetActive(document);
        return true;
    }

    /// <inheritdoc />
    public void Deactivate() => SetActive(null);

    /// <inheritdoc />
    public bool Close(string documentId)
    {
        if (Find(documentId) is not { } document)
        {
            return false;
        }

        var wasActive = ReferenceEquals(active, document);
        open.Remove(document);
        recent.Remove(document);
        if (wasActive)
        {
            SetActive(recent.FirstOrDefault());
        }

        ClosingDocument?.Invoke(document);
        Closed?.Invoke(document);
        return true;
    }

    IDocument? Find(string documentId) => open.Find(document => document.Id == documentId);

    void SetActive(IDocument? document)
    {
        if (ReferenceEquals(active, document))
        {
            return;
        }

        active = document;
        if (document is not null)
        {
            recent.Remove(document);
            recent.Insert(0, document);
        }

        ActivationSwitched?.Invoke(document);
        ActiveChanged?.Invoke(document);
    }
}

/// <summary>In-memory <see cref="IDocumentKindRegistry"/>.</summary>
public sealed class DocumentKindRegistry : IDocumentKindRegistry
{
    readonly Dictionary<string, DocumentKind> kinds = [];

    /// <summary>Every registered kind.</summary>
    public IEnumerable<DocumentKind> All => kinds.Values;

    /// <inheritdoc />
    public void Register(DocumentKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind.Id);
        kinds[kind.Id] = kind;
    }

    /// <summary>Looks a kind up by id.</summary>
    /// <param name="id">The kind id.</param>
    /// <returns>The kind, or null when none is registered under that id.</returns>
    public DocumentKind? Find(string id) => kinds.GetValueOrDefault(id);
}
