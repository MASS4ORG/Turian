namespace Turian.Engine.Save;

/// <summary>Concurrent in-memory storage with the same revision rules as cloud adapters.</summary>
public sealed class MemorySaveStorage : ISaveStorage, ISaveBackupStorage
{
    readonly object gate = new();
    readonly Dictionary<SaveSlot, StoredSave> saves = [];
    readonly Dictionary<SaveSlot, StoredSave> backups = [];

    /// <inheritdoc />
    public Task<StoredSave?> ReadAsync(SaveSlot slot, CancellationToken cancellationToken = default)
        => Read(slot, saves, cancellationToken);

    /// <inheritdoc />
    public Task<StoredSave?> ReadBackupAsync(SaveSlot slot, CancellationToken cancellationToken = default)
        => Read(slot, backups, cancellationToken);

    Task<StoredSave?> Read(SaveSlot slot, Dictionary<SaveSlot, StoredSave> source, CancellationToken token)
    {
        Validate(slot, token);
        lock (gate)
        {
            return Task.FromResult(source.TryGetValue(slot, out var save)
                ? new StoredSave([.. save.Data], save.Revision) : null);
        }
    }

    /// <inheritdoc />
    public Task<string> WriteAsync(SaveSlot slot, ReadOnlyMemory<byte> data, string? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        Validate(slot, cancellationToken);
        lock (gate)
        {
            saves.TryGetValue(slot, out var previous);
            CheckRevision(slot, previous?.Revision, expectedRevision);
            var bytes = data.ToArray();
            var revision = Guid.NewGuid().ToString("N");
            if (previous is not null)
            {
                backups[slot] = previous;
            }
            saves[slot] = new StoredSave(bytes, revision);
            return Task.FromResult(revision);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(SaveSlot slot, string expectedRevision, CancellationToken cancellationToken = default)
    {
        Validate(slot, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        lock (gate)
        {
            saves.TryGetValue(slot, out var previous);
            CheckRevision(slot, previous?.Revision, expectedRevision);
            saves.Remove(slot);
            backups.Remove(slot);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SaveSlot>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<SaveSlot>>(saves.Keys.OrderBy(static s => s.Name).ToArray());
        }
    }

    internal static void Validate(SaveSlot slot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(slot.Name);
    }

    internal static void CheckRevision(SaveSlot slot, string? actual, string? expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new SaveConflictException(slot);
        }
    }
}
