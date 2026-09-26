namespace Turian.Editor.Core;

/// <summary>
/// Prefab editing mode: opening an instance's prefab from a scene remembers where it came from, so the Scene view
/// can show a breadcrumb back. Switching documents any other way leaves the mode.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class PrefabStage : IDisposable
{
    readonly AssetWorkspace workspace;
    readonly AssetDatabase database;
    readonly List<AssetWrapper> trail = [];
    bool navigating;

    /// <summary>Follows the workspace, so picking another document leaves prefab mode.</summary>
    /// <param name="workspace">The open documents.</param>
    /// <param name="database">Resolves a prefab id to its asset.</param>
    public PrefabStage(AssetWorkspace workspace, AssetDatabase database)
    {
        this.workspace = workspace;
        this.database = database;
        workspace.Activated += OnActivated;
    }

    /// <summary>The documents to return to, outermost first; empty outside prefab mode.</summary>
    public IReadOnlyList<AssetWrapper> Trail => trail;

    /// <summary>Opens the prefab <paramref name="instance"/> was made from, remembering the current document.</summary>
    /// <param name="instance">A prefab instance root.</param>
    /// <returns>True when the prefab was found and opened.</returns>
    public bool OpenPrefab(Node instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.PrefabInstance?.Source.AssetId is not { } prefabId
            || !database.TryGetAsset(prefabId, out var record) || record is null
            || AssetReferenceQuery.CreateAsset(record) is not { } prefab)
            return false;

        var from = workspace.Active;
        Navigate(() => workspace.Open(prefab));
        if (from is not null && !ReferenceEquals(from, workspace.Active)) trail.Add(from);
        return true;
    }

    /// <summary>Returns to a document of the trail, leaving the ones opened after it.</summary>
    /// <param name="index">The position in <see cref="Trail"/>.</param>
    public void Return(int index)
    {
        if (index < 0 || index >= trail.Count) return;

        var target = trail[index];
        trail.RemoveRange(index, trail.Count - index);
        Navigate(() => workspace.Activate(target));
    }

    /// <inheritdoc />
    public void Dispose() => workspace.Activated -= OnActivated;

    void Navigate(Action action)
    {
        navigating = true;
        try
        {
            action();
        }
        finally
        {
            navigating = false;
        }
    }

    void OnActivated(AssetWrapper? document)
    {
        if (!navigating) trail.Clear();
    }
}
