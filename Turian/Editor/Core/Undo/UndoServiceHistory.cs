namespace Turian.Editor.Core;

/// <summary>
/// The project's <see cref="UndoService"/> as the history of a scene or asset tab, so those documents keep the one
/// chronological stack they share.
/// </summary>
/// <param name="service">Resolves the undo service when the history is used; null leaves the history empty.</param>
/// <param name="document">The asset id steps recorded here belong to; the current document when null.</param>
public sealed class UndoServiceHistory(Func<UndoService?> service, Guid? document = null) : IDocumentHistory
{
    /// <inheritdoc />
    public bool CanUndo => service()?.CanUndo ?? false;

    /// <inheritdoc />
    public bool CanRedo => service()?.CanRedo ?? false;

    /// <inheritdoc />
    public string? UndoLabel => service()?.UndoLabel;

    /// <inheritdoc />
    public string? RedoLabel => service()?.RedoLabel;

    /// <inheritdoc />
    public void Record(string label, Action undo, Action redo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(redo);

        // The service performs a step as it records it, and this change is already made.
        var made = false;
        service()?.Perform(label, [], () =>
        {
            if (made)
            {
                redo();
            }

            made = true;
        }, undo, document: document);
    }

    /// <inheritdoc />
    public void Undo() => service()?.Undo();

    /// <inheritdoc />
    public void Redo() => service()?.Redo();

    /// <summary>Forgets the document's steps; the project-wide history is left alone.</summary>
    public void Clear()
    {
        if (document is { } id)
        {
            service()?.History.Remove(id);
        }
    }
}
