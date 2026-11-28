namespace Turian.Editor.Core;

/// <summary>
/// A kind of document an editor brick contributes to the workspace through dependency injection. A source it claims
/// opens as its document instead of as an asset tab.
/// </summary>
public interface IWorkspaceDocumentKind
{
    /// <summary>
    /// Stable unique id, which every document of the kind reports as its <see cref="IWorkspaceDocument.Kind"/>.
    /// </summary>
    string Id { get; }

    /// <summary>What the kind is called in menus.</summary>
    string Title { get; }

    /// <summary>Whether this kind edits a source file; asked before every open, so it should be cheap.</summary>
    /// <param name="sourcePath">Absolute path of the source file.</param>
    bool CanOpen(string sourcePath);

    /// <summary>Opens a source; a failure is reported to the user and opens nothing.</summary>
    /// <param name="context">The source and what the workspace provides for it.</param>
    /// <returns>A new document whose id is the context's asset id.</returns>
    IWorkspaceDocument Open(DocumentOpenContext context);
}

/// <summary>What a document kind receives to open one document.</summary>
/// <param name="SourcePath">Absolute path of the source file.</param>
/// <param name="AssetId">The source's asset id.</param>
/// <param name="IsReadOnly">Whether the source may not be written.</param>
/// <param name="History">Where the document records its edits so that Undo and Redo reach them.</param>
public sealed record DocumentOpenContext(string SourcePath, Guid AssetId, bool IsReadOnly, IDocumentHistory History);
