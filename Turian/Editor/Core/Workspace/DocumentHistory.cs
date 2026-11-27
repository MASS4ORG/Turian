namespace Turian.Editor.Core;

/// <summary>An undo and redo stack that belongs to a single document.</summary>
/// <param name="limit">How many steps are kept; the oldest go first.</param>
public sealed class DocumentHistory(int limit = 200) : IDocumentHistory
{
    sealed class Step(string label, Action undo, Action redo)
    {
        public string Label => label;

        public Action Undo => undo;

        public Action Redo => redo;
    }

    readonly List<Step> done = [];
    readonly List<Step> undone = [];
    bool replaying;

    /// <inheritdoc />
    public bool CanUndo => done.Count > 0;

    /// <inheritdoc />
    public bool CanRedo => undone.Count > 0;

    /// <inheritdoc />
    public string? UndoLabel => CanUndo ? done[^1].Label : null;

    /// <inheritdoc />
    public string? RedoLabel => CanRedo ? undone[^1].Label : null;

    /// <inheritdoc />
    public void Record(string label, Action undo, Action redo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(redo);

        // A step being replayed changes the document through the same code that records edits.
        if (replaying)
        {
            return;
        }

        undone.Clear();
        done.Add(new Step(label, undo, redo));
        if (done.Count > limit)
        {
            done.RemoveAt(0);
        }
    }

    /// <inheritdoc />
    public void Undo() => Replay(done, undone, static step => step.Undo);

    /// <inheritdoc />
    public void Redo() => Replay(undone, done, static step => step.Redo);

    /// <inheritdoc />
    public void Clear()
    {
        done.Clear();
        undone.Clear();
    }

    // A step whose effect failed stays in place to be tried again.
    void Replay(List<Step> from, List<Step> to, Func<Step, Action> effect)
    {
        if (from.Count == 0)
        {
            return;
        }

        var step = from[^1];
        replaying = true;
        try
        {
            effect(step)();
        }
        catch (Exception exception)
        {
            Log.Logger.LogError(exception, "Could not replay {Step}", step.Label);
            return;
        }
        finally
        {
            replaying = false;
        }

        if (from.Remove(step))
        {
            to.Add(step);
        }
    }
}
