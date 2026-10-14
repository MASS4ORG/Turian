namespace Turian.Engine.Save;

/// <summary>Transfers save bytes using observed revisions to preserve concurrent edits.</summary>
public static class SaveTransfer
{
    /// <summary>Pushes or pulls an observed snapshot; null destination revision means create only.</summary>
    public static async Task<string> CopyAsync(ISaveStorage source, ISaveStorage destination, SaveSlot slot,
        string sourceRevision, string? destinationRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevision);
        var saved = await source.ReadAsync(slot, cancellationToken).ConfigureAwait(false);
        MemorySaveStorage.CheckRevision(slot, saved?.Revision, sourceRevision);
        return await destination.WriteAsync(slot, saved!.Data, destinationRevision, cancellationToken)
            .ConfigureAwait(false);
    }
}
