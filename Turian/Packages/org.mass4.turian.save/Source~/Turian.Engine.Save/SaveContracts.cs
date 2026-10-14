namespace Turian.Engine.Save;

/// <summary>Identifies a slot within a storage instance scoped to a game and account or server world.</summary>
public readonly record struct SaveSlot
{
    /// <summary>Creates a portable slot identifier, excluding paths and reserved punctuation.</summary>
    public SaveSlot(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 64 || name.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
        {
            throw new ArgumentException("Slots use 1–64 ASCII letters, digits, hyphens or underscores.", nameof(name));
        }
        Name = name.ToLowerInvariant();
    }

    /// <summary>The canonical lowercase storage key; a default slot must be rejected by storage adapters.</summary>
    public string Name { get; }
}

/// <summary>Describes a snapshot independently of its game schema and payload encoding.</summary>
public sealed record SaveMetadata
{
    /// <summary>UTC time supplied by the game or authoritative server.</summary>
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Elapsed game time.</summary>
    public TimeSpan PlayTime { get; init; }
    /// <summary>The game's release identifier.</summary>
    public string GameVersion { get; init; } = "";
    /// <summary>Optional thumbnail asset or object-store key, never an automatically loaded URL.</summary>
    public string? Thumbnail { get; init; }
    /// <summary>The authoritative simulation tick.</summary>
    public long Tick { get; init; }
    /// <summary>The explicit deterministic RNG state.</summary>
    public ulong RngState { get; init; }
    /// <summary>Base content and mod fingerprints keyed by stable identifiers.</summary>
    public Dictionary<string, string> ContentVersions { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Storage bytes and an opaque revision used for conditional writes.</summary>
/// <param name="Data">An owned copy of the saved bytes.</param>
/// <param name="Revision">The backend's version token.</param>
public sealed record StoredSave(byte[] Data, string Revision);

/// <summary>A decoded detached snapshot; apply it to live objects on the simulation thread.</summary>
/// <param name="State">Game-owned state.</param>
/// <param name="Metadata">Slot information.</param>
/// <param name="Revision">The revision to pass when updating or deleting this slot.</param>
public sealed record LoadedSave<T>(T State, SaveMetadata Metadata, string Revision);

/// <summary>Slot details for game menus without decoding or migrating the game payload.</summary>
/// <param name="Metadata">Display and compatibility metadata.</param>
/// <param name="SchemaVersion">The stored game schema version.</param>
/// <param name="SerializerId">The stored encoding identifier.</param>
/// <param name="Revision">The observed storage revision.</param>
public sealed record SaveSlotInfo(SaveMetadata Metadata, int SchemaVersion, string SerializerId, string Revision);

/// <summary>Storage adapters must compare and commit atomically, including across processes or devices.</summary>
public interface ISaveStorage
{
    /// <summary>Reads a slot, or returns null when absent.</summary>
    Task<StoredSave?> ReadAsync(SaveSlot slot, CancellationToken cancellationToken = default);
    /// <summary>Commits only if the revision matches; null means create only, never unconditional overwrite.</summary>
    Task<string> WriteAsync(SaveSlot slot, ReadOnlyMemory<byte> data, string? expectedRevision,
        CancellationToken cancellationToken = default);
    /// <summary>Deletes only the observed revision, rejecting concurrent changes.</summary>
    Task DeleteAsync(SaveSlot slot, string expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>Lists available slot identifiers in this storage scope.</summary>
    Task<IReadOnlyList<SaveSlot>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optional access to the previous committed generation without hiding primary corruption.</summary>
public interface ISaveBackupStorage
{
    /// <summary>Reads the previous generation, or null if none exists.</summary>
    Task<StoredSave?> ReadBackupAsync(SaveSlot slot, CancellationToken cancellationToken = default);
}

/// <summary>Reports that a conditional operation would overwrite an unobserved change.</summary>
public sealed class SaveConflictException : IOException
{
    /// <summary>Creates a conflict for the requested slot.</summary>
    public SaveConflictException(SaveSlot slot) : base($"Save slot '{slot.Name}' changed; reload before writing.") { }
}

/// <summary>Encodes DTOs without depending on scene serialization or a particular third-party library.</summary>
public interface ISaveSerializer
{
    /// <summary>Stable format identifier persisted in the envelope.</summary>
    string Id { get; }
    /// <summary>Encodes an owned snapshot.</summary>
    byte[] Serialize<T>(T state);
    /// <summary>Decodes a detached snapshot.</summary>
    T Deserialize<T>(ReadOnlySpan<byte> data);
}

/// <summary>Upgrades an encoded payload by one schema version; register implementations before use.</summary>
public interface ISaveMigration
{
    /// <summary>The source schema version.</summary>
    int FromVersion { get; }
    /// <summary>The encoding this migration understands.</summary>
    string SerializerId { get; }
    /// <summary>Produces the next version without changing stored bytes or live game objects.</summary>
    byte[] Upgrade(ReadOnlyMemory<byte> data);
}
