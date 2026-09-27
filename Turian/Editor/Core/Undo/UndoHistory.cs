namespace Turian.Editor.Core;

/// <summary>One recorded change: objects and the states they had before and after it.</summary>
/// <param name="Label">What the change was, for the Edit menu and the history.</param>
/// <param name="Before">Each changed object's state before the change.</param>
/// <param name="After">Each changed object's state after the change.</param>
public sealed record UndoStep(
    string Label,
    IReadOnlyDictionary<IdClass, ObjectState> Before,
    IReadOnlyDictionary<IdClass, ObjectState> After)
{
    /// <summary>When the step last took in a change, for merging a continuous edit into one step.</summary>
    public DateTime LastChanged { get; init; } = DateTime.UtcNow;

    /// <summary>Puts every object back as it was before the change.</summary>
    public void Undo()
    {
        foreach (var (target, state) in Before) state.Restore(target);
    }

    /// <summary>Puts every object back as it was after the change.</summary>
    public void Redo()
    {
        foreach (var (target, state) in After) state.Restore(target);
    }
}

/// <summary>
/// The undo and redo stacks of one document. A change to the same objects as the previous step, made soon after it,
/// joins that step, so a gizmo drag or a slider scrub is undone in one go.
/// </summary>
/// <param name="limit">How many steps are kept; the oldest go first.</param>
/// <param name="mergeWindow">How soon a change must follow the previous step to join it.</param>
public sealed class UndoHistory(int limit = 200, TimeSpan? mergeWindow = null)
{
    readonly TimeSpan window = mergeWindow ?? TimeSpan.FromSeconds(1);
    readonly List<UndoStep> undo = [];
    readonly List<UndoStep> redo = [];

    /// <summary>Whether there is a step to undo.</summary>
    public bool CanUndo => undo.Count > 0;

    /// <summary>Whether there is a step to redo.</summary>
    public bool CanRedo => redo.Count > 0;

    /// <summary>The steps that can be undone, oldest first.</summary>
    public IReadOnlyList<UndoStep> UndoSteps => undo;

    /// <summary>The steps that can be redone, the next one last.</summary>
    public IReadOnlyList<UndoStep> RedoSteps => redo;

    /// <summary>Records a change that has already happened, dropping anything that could be redone.</summary>
    /// <param name="step">The change.</param>
    /// <param name="mergeable">Whether it may join the previous step.</param>
    public void Push(UndoStep step, bool mergeable = true)
    {
        ArgumentNullException.ThrowIfNull(step);

        redo.Clear();
        if (mergeable && undo.Count > 0 && CanMerge(undo[^1], step))
        {
            undo[^1] = undo[^1] with { After = step.After, LastChanged = step.LastChanged };
            return;
        }

        undo.Add(step);
        if (undo.Count > limit) undo.RemoveAt(0);
    }

    /// <summary>Undoes the latest step.</summary>
    /// <returns>The step undone, or null when there was none.</returns>
    public UndoStep? Undo()
    {
        if (undo.Count == 0) return null;

        var step = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        step.Undo();
        redo.Add(step);
        return step;
    }

    /// <summary>Redoes the latest undone step.</summary>
    /// <returns>The step redone, or null when there was none.</returns>
    public UndoStep? Redo()
    {
        if (redo.Count == 0) return null;

        var step = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        step.Redo();
        undo.Add(step);
        return step;
    }

    /// <summary>Forgets every step, when the objects they refer to no longer exist.</summary>
    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }

    bool CanMerge(UndoStep previous, UndoStep next) =>
        next.LastChanged - previous.LastChanged <= window
        && previous.Label == next.Label
        && previous.After.Count == next.Before.Count
        && next.Before.Keys.All(previous.After.ContainsKey);
}
