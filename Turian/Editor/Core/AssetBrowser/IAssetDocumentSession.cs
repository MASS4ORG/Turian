namespace Turian.Editor.Core;

/// <summary>
/// Optional companion of <see cref="IAssetDocumentEditor"/> for an editor that owns a document's unsaved state. The
/// studio routes Save, Save All, close prompts and Undo/Redo through it; the editor keeps its own undo stack. The
/// session with focus is the one whose panel Gaya reports as focused, so the editor never reports focus itself.
/// </summary>
public interface IAssetDocumentSession
{
    /// <summary>The absolute source path of the open document, or null when none is open.</summary>
    string? DocumentPath { get; }

    /// <summary>Whether the document has edits that are not written to its source yet.</summary>
    bool IsDirty { get; }

    /// <summary>Whether <see cref="Undo"/> has an edit to revert.</summary>
    bool CanUndo { get; }

    /// <summary>Whether <see cref="Redo"/> has an edit to reapply.</summary>
    bool CanRedo { get; }

    /// <summary>Writes the document to its source; on failure the document stays dirty.</summary>
    /// <param name="error">Why the write failed, or null on success.</param>
    /// <returns>True when the source was written.</returns>
    bool TrySave(out string? error);

    /// <summary>Drops the unsaved edits and closes the document.</summary>
    void Discard();

    /// <summary>Reverts the editor's latest edit.</summary>
    void Undo();

    /// <summary>Reapplies the editor's latest reverted edit.</summary>
    void Redo();
}
