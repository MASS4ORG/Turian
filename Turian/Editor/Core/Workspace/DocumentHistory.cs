namespace Turian.Editor.Core;

/// <summary>An undo and redo stack that belongs to a single document.</summary>
/// <param name="limit">How many steps are kept; the oldest go first.</param>
public sealed class DocumentHistory(int limit = 200) : IDocumentHistory
{
    sealed class Step(string label)
    {
        public string Label => label;

        public List<(Action Undo, Action Redo)> Changes { get; } = [];

        public void Undo()
        {
            for (var i = Changes.Count - 1; i >= 0; i--)
            {
                try
                {
                    Changes[i].Undo();
                }
                catch
                {
                    // The step stays undoable as a whole, so what was already reverted is made again.
                    Restore(Changes.Skip(i + 1).Select(static change => change.Redo));
                    throw;
                }
            }
        }

        public void Redo()
        {
            for (var i = 0; i < Changes.Count; i++)
            {
                try
                {
                    Changes[i].Redo();
                }
                catch
                {
                    Restore(Changes.Take(i).Reverse().Select(static change => change.Undo));
                    throw;
                }
            }
        }

        static void Restore(IEnumerable<Action> effects)
        {
            foreach (var effect in effects)
            {
                try
                {
                    effect();
                }
                catch (Exception exception)
                {
                    Log.Logger.LogError(exception, "Could not restore part of a step");
                }
            }
        }
    }

    readonly List<Step> done = [];
    readonly List<Step> undone = [];
    bool replaying;
    int groups;
    string groupLabel = string.Empty;

    // The step the open group collects into, once it has a change.
    Step? grouped;

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
        if (grouped is not null && done.Count > 0 && ReferenceEquals(done[^1], grouped))
        {
            grouped.Changes.Add((undo, redo));
            return;
        }

        var step = new Step(groups > 0 ? groupLabel : label);
        step.Changes.Add((undo, redo));
        done.Add(step);
        grouped = groups > 0 ? step : null;
        if (done.Count > limit)
        {
            done.RemoveAt(0);
        }
    }

    /// <inheritdoc />
    public IDisposable BeginGroup(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (groups++ == 0)
        {
            groupLabel = label;
        }

        return new ContributionLifetime(() =>
        {
            if (--groups == 0)
            {
                grouped = null;
            }
        });
    }

    /// <inheritdoc />
    public void Undo() => Replay(done, undone, static step => step.Undo());

    /// <inheritdoc />
    public void Redo() => Replay(undone, done, static step => step.Redo());

    /// <inheritdoc />
    public void Clear()
    {
        done.Clear();
        undone.Clear();
    }

    // A step whose effect failed stays in place to be tried again.
    void Replay(List<Step> from, List<Step> to, Action<Step> effect)
    {
        if (from.Count == 0)
        {
            return;
        }

        var step = from[^1];
        replaying = true;
        try
        {
            effect(step);
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
