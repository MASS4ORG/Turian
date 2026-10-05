namespace Turian.Editor.Core;

/// <summary>Stores an ordered selection of scene objects or assets with one active object.</summary>
public sealed class SelectionService
{
    IReadOnlyList<object> objects = Array.Empty<object>();

    /// <summary>The selected objects in selection order.</summary>
    public IReadOnlyList<object> Objects => objects;

    /// <summary>The active object, used for keyboard focus and the active transform pivot.</summary>
    public object? ActiveObject { get; private set; }

    /// <summary>Raised once when the selection or active object changes.</summary>
    public event Action? Changed;

    /// <summary>Replaces the selection, removing duplicates by identity and retaining the requested active object.</summary>
    public void SetObjects(IEnumerable<object> targets, object? active = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var next = targets.Distinct(ReferenceEqualityComparer.Instance).ToArray();
        var nextActive = next.Contains(active, ReferenceEqualityComparer.Instance) ? active : next.LastOrDefault();
        if (ReferenceEquals(ActiveObject, nextActive)
            && objects.SequenceEqual(next, ReferenceEqualityComparer.Instance)) return;
        objects = Array.AsReadOnly(next);
        ActiveObject = nextActive;
        Changed?.Invoke();
    }

    /// <summary>Selects one object, or clears the selection when the target is null.</summary>
    public void Select(object? target) => SetObjects(target is null ? [] : [target], target);

    /// <summary>Adds an object and makes it active without changing the order of existing objects.</summary>
    public void Add(object target) => SetObjects([.. objects, target], target);

    /// <summary>Removes a selected object, or adds it and makes it active.</summary>
    public void Toggle(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (objects.Contains(target, ReferenceEqualityComparer.Instance))
            SetObjects(objects.Where(item => !ReferenceEquals(item, target)), ActiveObject);
        else Add(target);
    }

    /// <summary>Selects a contiguous range from the supplied visible order, optionally keeping the existing selection.</summary>
    public void SelectRange(IReadOnlyList<object> visible, object anchor, object target, bool additive = false)
    {
        ArgumentNullException.ThrowIfNull(visible);
        var start = IndexOf(visible, anchor);
        var end = IndexOf(visible, target);
        if (end < 0) return;
        if (start < 0) start = end;
        var range = visible.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1);
        SetObjects(additive ? objects.Concat(range) : range, target);
    }

    /// <summary>Returns selected nodes whose ancestors are not also selected, so hierarchy operations run once.</summary>
    public static IReadOnlyList<Node> TopLevelNodes(IEnumerable<Node> nodes)
    {
        var ordered = nodes.Distinct().ToArray();
        var selected = ordered.ToHashSet();
        return [.. ordered.Where(node => !HasSelectedAncestor(node, selected))];
    }

    static bool HasSelectedAncestor(Node node, HashSet<Node> selected)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (selected.Contains(parent)) return true;
        return false;
    }

    static int IndexOf(IReadOnlyList<object> items, object target)
    {
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], target)) return i;
        return -1;
    }
}
