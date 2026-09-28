namespace Turian.Editor.Core;

/// <summary>
/// Saves edits to authored assets — data assets, materials, project settings — without an Apply step, the way Unity
/// saves a ScriptableObject. An edit is written once the user pauses, so dragging a value does not rewrite the file
/// every frame. Import settings are not saved here: applying them reimports the file, so they keep Apply and Revert.
/// </summary>
/// <param name="inspections">Writes an edited asset back and reimports it.</param>
/// <param name="settleTime">How long edits must pause before they are written. Defaults to half a second.</param>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class AssetAutoSave(AssetInspectionService inspections, TimeSpan? settleTime = null)
{
    readonly TimeSpan settle = settleTime ?? TimeSpan.FromMilliseconds(500);
    readonly Dictionary<string, AssetInspection> pending = new(StringComparer.Ordinal);
    readonly Stopwatch sinceChange = new();

    /// <summary>Whether an edit is waiting to be written.</summary>
    public bool HasPending => pending.Count > 0;

    /// <summary>Notes an edit to an inspected asset; import settings are ignored.</summary>
    /// <param name="inspection">The inspection whose target was edited.</param>
    public void MarkChanged(AssetInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        if (!inspection.IsPayload) return;
        pending[inspection.AbsolutePath] = inspection;
        sinceChange.Restart();
    }

    /// <summary>Forgets the pending edits without writing them, when the user chooses not to save.</summary>
    public void Discard() => pending.Clear();

    /// <summary>Writes the pending edits once they have settled. The studio calls it once a frame.</summary>
    /// <param name="force">Writes them now, such as when the studio closes.</param>
    public void Flush(bool force = false)
    {
        if (pending.Count == 0 || (!force && sinceChange.Elapsed < settle)) return;

        var saving = pending.Values.ToList();
        pending.Clear();
        foreach (var inspection in saving) inspections.Apply(inspection);
    }
}
