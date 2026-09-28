namespace Turian.Editor.Core;

/// <summary>
/// One recorded change: the document it happened in, the objects it changed with their states before and after, and
/// optionally an effect beyond those objects, such as a prefab file it rewrote.
/// </summary>
/// <param name="Label">What the change was, for the Edit menu and the history.</param>
/// <param name="Document">The asset id of the scene or asset the change belongs to.</param>
/// <param name="Before">Each changed object's state before the change.</param>
/// <param name="After">Each changed object's state after the change.</param>
public sealed record UndoStep(
    string Label,
    Guid Document,
    IReadOnlyDictionary<IdClass, ObjectState> Before,
    IReadOnlyDictionary<IdClass, ObjectState> After)
{
    /// <summary>Identifies the step, and stays the same when later changes merge into it.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>When the step last took in a change, for merging a continuous edit into one step.</summary>
    public DateTime LastChanged { get; init; } = DateTime.UtcNow;

    /// <summary>Whether later changes may still join this step; not once its document was saved after it.</summary>
    public bool Sealed { get; init; }

    /// <summary>
    /// This step with <paramref name="next"/> joined into it: the earliest before states, the latest after ones.
    /// </summary>
    /// <param name="next">A later change.</param>
    /// <returns>The merged step.</returns>
    public UndoStep Merge(UndoStep next)
    {
        ArgumentNullException.ThrowIfNull(next);

        var before = new Dictionary<IdClass, ObjectState>(Before, ReferenceEqualityComparer.Instance);
        var after = new Dictionary<IdClass, ObjectState>(After, ReferenceEqualityComparer.Instance);
        foreach (var (target, state) in next.Before) before.TryAdd(target, state);
        foreach (var (target, state) in next.After) after[target] = state;
        return this with { Before = before, After = after, LastChanged = next.LastChanged };
    }

    /// <summary>Reverts what the step did outside its objects; runs before their states are restored.</summary>
    public Action? UndoEffect { get; init; }

    /// <summary>Repeats what the step did outside its objects; runs before their states are restored.</summary>
    public Action? RedoEffect { get; init; }

    /// <summary>The step with its objects swapped for their replacements after a rebuild.</summary>
    /// <param name="map">Returns the replacement of an object from before the rebuild.</param>
    /// <returns>The remapped step.</returns>
    public UndoStep Remap(Func<IdClass, IdClass> map) => this with
    {
        Before = RemapStates(Before, map),
        After = RemapStates(After, map),
    };

    static Dictionary<IdClass, ObjectState> RemapStates(IReadOnlyDictionary<IdClass, ObjectState> states,
        Func<IdClass, IdClass> map)
    {
        var remapped = new Dictionary<IdClass, ObjectState>(ReferenceEqualityComparer.Instance);
        foreach (var (target, state) in states)
        {
            var replacement = map(target);
            remapped[replacement] = state.Remap(replacement.GetType(), map);
        }

        return remapped;
    }
}

/// <summary>
/// The project's undo and redo stacks, one for every document, as in Unity. A change to the same objects as the
/// previous step, made soon after it, joins that step, so a gizmo drag or a slider scrub is undone in one go.
/// </summary>
/// <param name="limit">How many steps are kept; the oldest go first.</param>
/// <param name="mergeWindow">How soon a change must follow the previous step to join it.</param>
public sealed class UndoHistory(int limit = 200, TimeSpan? mergeWindow = null)
{
    readonly TimeSpan window = mergeWindow ?? TimeSpan.FromSeconds(1);
    readonly List<UndoStep> undo = [];
    readonly List<UndoStep> redo = [];
    List<UndoStep> droppedRedo = [];

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

        droppedRedo = [.. redo];
        redo.Clear();
        if (mergeable && undo.Count > 0 && CanMerge(undo[^1], step))
        {
            undo[^1] = undo[^1].Merge(step);
            return;
        }

        undo.Add(step);
        if (undo.Count > limit) undo.RemoveAt(0);
    }

    /// <summary>Joins a change into the latest step whatever its label or timing, as within one gesture.</summary>
    /// <param name="step">The change.</param>
    public void MergeIntoLatest(UndoStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        redo.Clear();
        if (undo.Count == 0) undo.Add(step);
        else undo[^1] = undo[^1].Merge(step);
    }

    /// <summary>Takes back the latest push, restoring the redo steps it dropped, when its change failed.</summary>
    public void CancelLatest()
    {
        if (undo.Count > 0) undo.RemoveAt(undo.Count - 1);
        redo.Clear();
        redo.AddRange(droppedRedo);
    }

    /// <summary>The id of a document's latest undoable step, or <see cref="System.Guid.Empty"/> when it has none.</summary>
    /// <param name="document">The document's asset id.</param>
    /// <returns>The step id.</returns>
    public Guid LatestFor(Guid document) =>
        undo.LastOrDefault(step => step.Document == document)?.Id ?? Guid.Empty;

    /// <summary>Stops a document's latest step from taking in later changes, once the document is saved.</summary>
    /// <param name="document">The document's asset id.</param>
    public void Seal(Guid document)
    {
        var index = undo.FindLastIndex(step => step.Document == document);
        if (index >= 0) undo[index] = undo[index] with { Sealed = true };
    }

    /// <summary>Undoes the latest step. When its effect throws, the step stays where it was.</summary>
    /// <returns>The step undone, or null when there was none.</returns>
    public UndoStep? Undo()
    {
        if (undo.Count == 0) return null;

        redo.Add(undo[^1]);
        undo.RemoveAt(undo.Count - 1);

        // The effect may rebuild a scene, which remaps the step where it now sits.
        try
        {
            redo[^1].UndoEffect?.Invoke();
        }
        catch
        {
            undo.Add(redo[^1]);
            redo.RemoveAt(redo.Count - 1);
            throw;
        }

        var step = redo[^1];
        foreach (var (target, state) in step.Before) state.Restore(target);
        return step;
    }

    /// <summary>Redoes the latest undone step. When its effect throws, the step stays where it was.</summary>
    /// <returns>The step redone, or null when there was none.</returns>
    public UndoStep? Redo()
    {
        if (redo.Count == 0) return null;

        undo.Add(redo[^1]);
        redo.RemoveAt(redo.Count - 1);

        try
        {
            undo[^1].RedoEffect?.Invoke();
        }
        catch
        {
            redo.Add(undo[^1]);
            undo.RemoveAt(undo.Count - 1);
            throw;
        }

        var step = undo[^1];
        foreach (var (target, state) in step.After) state.Restore(target);
        return step;
    }

    /// <summary>Swaps a document's objects for their replacements after its scene was rebuilt.</summary>
    /// <param name="document">The rebuilt scene's asset id.</param>
    /// <param name="map">Returns the replacement of an object from before the rebuild.</param>
    public void Remap(Guid document, Func<IdClass, IdClass> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        for (var i = 0; i < undo.Count; i++)
            if (undo[i].Document == document) undo[i] = undo[i].Remap(map);
        for (var i = 0; i < redo.Count; i++)
            if (redo[i].Document == document) redo[i] = redo[i].Remap(map);
    }

    /// <summary>Forgets a document's steps, when its objects are gone for good.</summary>
    /// <param name="document">The closed document's asset id.</param>
    public void Remove(Guid document)
    {
        undo.RemoveAll(step => step.Document == document);
        redo.RemoveAll(step => step.Document == document);
    }

    /// <summary>Forgets every step.</summary>
    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }

    bool CanMerge(UndoStep previous, UndoStep next) =>
        previous is { Sealed: false, UndoEffect: null } && next.UndoEffect is null
                                                        && next.LastChanged - previous.LastChanged <= window
                                                        && previous.Label == next.Label
                                                        && previous.Document == next.Document
                                                        && previous.After.Count == next.Before.Count
                                                        && next.Before.Keys.All(previous.After.ContainsKey);
}
