using System.Diagnostics;

namespace Turian.Editor.Core;

/// <summary>
/// Knows which values of the selected prefab instance differ from its prefab, so the inspector can mark them. The
/// difference is taken for the outermost instance around the selection, which covers the prefabs nested in it.
/// </summary>
/// <param name="loadPrefab">Returns a prefab's serialized hierarchy by asset id, or null when it is missing.</param>
/// <param name="refreshInterval">
/// The shortest time between two comparisons. Scrubbing a value changes it every frame, and each comparison
/// serializes the whole instance. Defaults to 150 ms.
/// </param>
public sealed class PrefabOverrideTracker(Func<Guid, string?> loadPrefab, TimeSpan? refreshInterval = null)
{
    readonly TimeSpan minimumInterval = refreshInterval ?? TimeSpan.FromMilliseconds(150);
    readonly Stopwatch sinceRefresh = Stopwatch.StartNew();
    Node? instanceRoot;
    PrefabInstanceDiff? diff;
    bool stale;

    /// <summary>The outermost prefab instance being tracked, or null when the selection is in none.</summary>
    public Node? InstanceRoot => instanceRoot;

    /// <summary>Follows the selection: tracks the outermost prefab instance that contains <paramref name="node"/>.</summary>
    /// <param name="node">The selected node, or null.</param>
    public void Track(Node? node)
    {
        var root = node is null ? null : OutermostInstance(node);
        if (ReferenceEquals(root, instanceRoot)) return;

        instanceRoot = root;
        diff = null;
        stale = root is not null;
    }

    /// <summary>Marks the comparison out of date after an edit to the tracked instance.</summary>
    public void Invalidate() => stale = instanceRoot is not null;

    /// <summary>The tracked instance's differences from its prefab, or null when there is no instance.</summary>
    public PrefabInstanceDiff? Diff
    {
        get
        {
            if (instanceRoot is null) return null;
            if (stale && (diff is null || sinceRefresh.Elapsed >= minimumInterval))
            {
                diff = Compute(instanceRoot);
                stale = false;
                sinceRefresh.Restart();
            }

            return diff;
        }
    }

    /// <summary>Whether <paramref name="member"/> of <paramref name="target"/> differs from the prefab.</summary>
    /// <param name="target">A node or component.</param>
    /// <param name="member">The serialized member name.</param>
    /// <returns>True when the value is an override.</returns>
    public bool IsOverridden(object target, string member) =>
        target is IdClass obj && Diff is { } current && current.Overrides.Contains((obj.Id, member));

    /// <summary>Whether the instance added <paramref name="target"/> rather than getting it from the prefab.</summary>
    /// <param name="target">A node or component.</param>
    /// <returns>True for an addition.</returns>
    public bool IsAdded(object target) => target is IdClass obj && Diff is { } current && current.Added.Contains(obj.Id);

    /// <summary>The outermost node linked to a prefab among <paramref name="node"/> and its ancestors.</summary>
    /// <param name="node">Where to start.</param>
    /// <returns>The instance root, or null when the node is in no instance.</returns>
    public static Node? OutermostInstance(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Node? outermost = null;
        for (Node? current = node; current is not null; current = current.Parent)
            if (current.PrefabInstance is not null) outermost = current;
        return outermost;
    }

    PrefabInstanceDiff Compute(Node root)
    {
        try
        {
            return PrefabInstances.Diff(Serializer.Serialize(root), loadPrefab);
        }
        catch (Exception exception)
        {
            Log.Logger.LogWarning(exception, "Could not compare prefab instance {Name} with its prefab", root.Name);
            return new PrefabInstanceDiff();
        }
    }
}
