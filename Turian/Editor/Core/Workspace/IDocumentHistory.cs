namespace Turian.Editor.Core;

/// <summary>
/// The undo and redo steps reachable from one document. The workspace hands one to each document, and Edit &gt; Undo
/// and Redo act on the active document's.
/// </summary>
public interface IDocumentHistory
{
    /// <summary>Whether there is a step to undo.</summary>
    bool CanUndo { get; }

    /// <summary>Whether there is a step to redo.</summary>
    bool CanRedo { get; }

    /// <summary>The label of the step Undo would revert, or null.</summary>
    string? UndoLabel { get; }

    /// <summary>The label of the step Redo would reapply, or null.</summary>
    string? RedoLabel { get; }

    /// <summary>Records a change that has already been made.</summary>
    /// <param name="label">What the change was, for the Edit menu.</param>
    /// <param name="undo">Reverts the change.</param>
    /// <param name="redo">Makes the change again.</param>
    void Record(string label, Action undo, Action redo);

    /// <summary>Reverts the latest step.</summary>
    void Undo();

    /// <summary>Reapplies the latest undone step.</summary>
    void Redo();

    /// <summary>Forgets the document's steps, when the content they changed has been replaced.</summary>
    void Clear();
}
