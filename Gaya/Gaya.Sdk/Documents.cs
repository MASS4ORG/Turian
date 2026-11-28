namespace Gaya.Sdk;

/// <summary>
/// A document the host has open, such as a scene or a script. The host owns what it means; the workbench only uses the
/// identity to pick a layout and to give each document its own instances of the panels bound to it.
/// </summary>
public interface IDocument
{
    /// <summary>Stable unique id of the open document, for as long as it stays open.</summary>
    string Id { get; }

    /// <summary>The <see cref="DocumentKind.Id"/> of the kind this document belongs to; decides its layout and panels.</summary>
    string Kind { get; }

    /// <summary>The name shown for the document, such as in a tab.</summary>
    string Title { get; }
}

/// <summary>A plain <see cref="IDocument"/> for hosts that have no document type of their own.</summary>
/// <param name="Id">Stable unique id of the open document.</param>
/// <param name="Kind">The id of the document's kind.</param>
/// <param name="Title">The name shown for the document.</param>
public sealed record DocumentInfo(string Id, string Kind, string Title) : IDocument;

/// <summary>
/// A type of document. Each kind has a dock layout of its own, so switching to a document of another kind swaps the
/// arrangement while switching between documents of one kind keeps it.
/// </summary>
/// <param name="Id">Stable unique id, e.g. <c>gaya.example.scene</c>.</param>
/// <param name="Title">What the kind is called in menus.</param>
public sealed record DocumentKind(string Id, string Title)
{
    /// <summary>
    /// Where this kind's layout docks a panel the first time it opens, by panel id, taking the place of the panel's own
    /// <see cref="PanelDescriptor.DefaultPlacement"/>. Panels not listed use their own placement.
    /// </summary>
    public IReadOnlyDictionary<string, PanelPlacement> Placements { get; init; } =
        new Dictionary<string, PanelPlacement>();
}

/// <summary>Collects <see cref="DocumentKind"/> contributions.</summary>
public interface IDocumentKindRegistry
{
    /// <summary>Adds a document kind. Re-adding the same id replaces the earlier one.</summary>
    /// <param name="kind">The kind to register.</param>
    void Register(DocumentKind kind);

    /// <summary>Removes a document kind, for a contributor whose set changes at runtime.</summary>
    /// <param name="kindId">The kind to remove.</param>
    void Remove(string kindId);
}

/// <summary>
/// The documents the host has open and which one is active. Hosts call it to open, activate and close documents; the
/// workbench follows by switching layout and panel instances.
/// </summary>
public interface IDocumentService
{
    /// <summary>The document whose panels are shown, or null when none is active and the no-document layout applies.</summary>
    IDocument? Active { get; }

    /// <summary>Every open document, in the order they were opened.</summary>
    IReadOnlyList<IDocument> Documents { get; }

    /// <summary>Raised after the active document changed, with the new one or null.</summary>
    event Action<IDocument?>? ActiveChanged;

    /// <summary>Raised after a document closed and its panel instances were released.</summary>
    event Action<IDocument>? Closed;

    /// <summary>Opens a document and makes it active; a document already open under the same id is just activated.</summary>
    /// <param name="document">The document to open.</param>
    void Open(IDocument document);

    /// <summary>Makes an open document the active one.</summary>
    /// <param name="documentId">The id of the document to show.</param>
    /// <returns>False when no open document has that id.</returns>
    bool Activate(string documentId);

    /// <summary>Makes no document active, so the no-document layout applies; the documents stay open.</summary>
    void Deactivate();

    /// <summary>
    /// Closes a document, which disposes its panel instances. When it was active, the most recently active remaining
    /// document becomes active, or none.
    /// </summary>
    /// <param name="documentId">The id of the document to close.</param>
    /// <returns>False when no open document has that id.</returns>
    bool Close(string documentId);
}

/// <summary>
/// Optional companion of <see cref="IPanel"/> for a document panel holding heavy resources, such as GPU targets. The
/// workbench suspends the panels of documents that have not been active recently and does nothing else: the panel
/// rebuilds what it released the next time it renders, and disposes the rest through <see cref="IDisposable"/> when its
/// document closes.
/// </summary>
public interface ISuspendablePanel
{
    /// <summary>Releases the heavy resources; the next <see cref="IPanel.Render"/> must be able to rebuild them.</summary>
    void Suspend();
}
