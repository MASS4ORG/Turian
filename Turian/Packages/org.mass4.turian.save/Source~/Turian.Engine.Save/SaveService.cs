using System.Text.Json;

namespace Turian.Engine.Save;

/// <summary>Persists detached DTOs on worker threads and validates them before game restoration.</summary>
public sealed class SaveService
{
    readonly ISaveStorage storage;
    readonly string schemaId;
    readonly int schemaVersion;
    readonly ISaveSerializer writer;
    readonly Dictionary<string, ISaveSerializer> serializers;
    readonly Dictionary<(string, int), ISaveMigration> migrations;
    readonly int maximumBytes;
    readonly ISaveContainerCodec containerCodec;
    readonly ISaveContainerCodec[] containerReaders;

    /// <summary>Freezes the serializer and migration registry using the default JSON container.</summary>
    public SaveService(ISaveStorage storage, string schemaId, int schemaVersion = 1,
        ISaveSerializer? writer = null, IEnumerable<ISaveSerializer>? readers = null,
        IEnumerable<ISaveMigration>? migrations = null, int maximumBytes = 64 * 1024 * 1024)
        : this(storage, schemaId, null, schemaVersion, writer, readers, migrations, maximumBytes)
    {
    }

    /// <summary>Freezes payload and container registries; a null container codec selects the JSON default.</summary>
    public SaveService(ISaveStorage storage, string schemaId, ISaveContainerCodec? containerCodec,
        int schemaVersion = 1, ISaveSerializer? writer = null, IEnumerable<ISaveSerializer>? readers = null,
        IEnumerable<ISaveMigration>? migrations = null, int maximumBytes = 64 * 1024 * 1024,
        IEnumerable<ISaveContainerCodec>? containerReaders = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        this.storage = storage;
        this.schemaId = schemaId;
        this.schemaVersion = schemaVersion;
        this.writer = writer ?? new JsonSaveSerializer();
        this.maximumBytes = maximumBytes;
        serializers = (readers ?? []).ToDictionary(static r => r.Id, StringComparer.Ordinal);
        serializers[this.writer.Id] = this.writer;
        this.migrations = (migrations ?? []).ToDictionary(static m => (m.SerializerId, m.FromVersion));
        this.containerCodec = containerCodec ?? new JsonSaveContainerCodec();
        this.containerReaders = ContainerReaders(this.containerCodec, containerReaders);
    }

    static ISaveContainerCodec[] ContainerReaders(ISaveContainerCodec writer,
        IEnumerable<ISaveContainerCodec>? readers)
    {
        var jsonContainer = new JsonSaveContainerCodec();
        var codecs = (readers ?? []).ToDictionary(static c => c.Id, StringComparer.Ordinal);
        codecs.TryAdd(jsonContainer.Id, jsonContainer);
        codecs.Remove(writer.Id);
        return [.. codecs.Values.Prepend(writer)
            .OrderBy(static codec => codec is JsonSaveContainerCodec)];
    }

    /// <summary>Queues an owned snapshot captured on the simulation thread.</summary>
    public async Task<string> SaveAsync<T>(SaveSlot slot, T snapshot, SaveMetadata metadata,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(metadata);
        var bytes = await Task.Run(() => Encode(snapshot, metadata), cancellationToken).ConfigureAwait(false);
        return await storage.WriteAsync(slot, bytes, expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads detached data; an optional compatibility check runs before payload decoding.</summary>
    public async Task<LoadedSave<T>?> LoadAsync<T>(SaveSlot slot, Action<SaveMetadata>? validateCompatibility = null,
        CancellationToken cancellationToken = default)
    {
        var saved = await storage.ReadAsync(slot, cancellationToken).ConfigureAwait(false);
        return await DecodeAsync<T>(saved, validateCompatibility, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads and checks slot metadata without running game migrations or deserializing its payload.</summary>
    public async Task<SaveSlotInfo?> InspectAsync(SaveSlot slot, CancellationToken cancellationToken = default)
    {
        var saved = await storage.ReadAsync(slot, cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            return null;
        }
        return await Task.Run(() => Inspect(saved), cancellationToken).ConfigureAwait(false);
    }

    SaveSlotInfo Inspect(StoredSave saved)
    {
        var body = ReadBody(saved.Data);
        ValidateMetadata(body.Metadata);
        return new SaveSlotInfo(body.Metadata, body.SchemaVersion, body.SerializerId, saved.Revision);
    }

    /// <summary>Loads a backup whose revision identifies that generation, rather than the primary slot.</summary>
    public async Task<LoadedSave<T>?> LoadBackupAsync<T>(SaveSlot slot,
        Action<SaveMetadata>? validateCompatibility = null, CancellationToken cancellationToken = default)
    {
        if (storage is not ISaveBackupStorage backups)
        {
            throw new NotSupportedException("This storage does not expose backups.");
        }
        var saved = await backups.ReadBackupAsync(slot, cancellationToken).ConfigureAwait(false);
        return await DecodeAsync<T>(saved, validateCompatibility, cancellationToken).ConfigureAwait(false);
    }

    Task<LoadedSave<T>?> DecodeAsync<T>(StoredSave? saved, Action<SaveMetadata>? validate,
        CancellationToken token)
    {
        if (saved is null)
        {
            return Task.FromResult<LoadedSave<T>?>(null);
        }
        return Task.Run<LoadedSave<T>?>(() => Decode<T>(saved, validate), token);
    }

    byte[] Encode<T>(T snapshot, SaveMetadata metadata)
    {
        ValidateMetadata(metadata);
        var payload = writer.Serialize(snapshot);
        CheckSize(payload.Length);
        var bytes = containerCodec.Encode(new SaveContainer(schemaId, schemaVersion, writer.Id, metadata, payload));
        CheckSize(bytes.Length);
        return bytes;
    }

    LoadedSave<T> Decode<T>(StoredSave saved, Action<SaveMetadata>? validate)
    {
        try
        {
            var body = ReadBody(saved.Data);
            ValidateMetadata(body.Metadata);
            validate?.Invoke(body.Metadata);
            var payload = Upgrade(body);
            return new LoadedSave<T>(serializers[body.SerializerId].Deserialize<T>(payload),
                body.Metadata, saved.Revision);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid save JSON.", error);
        }
    }

    SaveContainer ReadBody(byte[] bytes)
    {
        CheckSize(bytes.Length);
        var codec = containerReaders.FirstOrDefault(reader => reader.CanRead(bytes))
            ?? throw new InvalidDataException("Unsupported save container format.");
        var body = codec.Decode(bytes, maximumBytes)
            ?? throw new InvalidDataException("Container codec returned null.");
        ValidateBody(body);
        CheckSize(body.Payload.Length);
        return body;
    }

    void ValidateBody(SaveContainer body)
    {
        if (body.SchemaId != schemaId || body.SchemaVersion < 1 || body.SchemaVersion > schemaVersion)
        {
            throw new InvalidDataException("Unsupported save schema.");
        }
        ValidatePayload(body);
    }

    void ValidatePayload(SaveContainer body)
    {
        if (body.Payload is null || body.Metadata is null || body.SerializerId is null ||
            !serializers.ContainsKey(body.SerializerId))
        {
            throw new InvalidDataException("Missing payload or unsupported save serializer.");
        }
    }

    byte[] Upgrade(SaveContainer body)
    {
        var payload = body.Payload;
        for (var version = body.SchemaVersion; version < schemaVersion; version++)
        {
            if (!migrations.TryGetValue((body.SerializerId, version), out var migration))
            {
                throw new InvalidDataException($"Missing {body.SerializerId} migration from schema {version}.");
            }
            payload = migration.Upgrade(payload) ?? throw new InvalidDataException("Migration returned null.");
            CheckSize(payload.Length);
        }
        return payload;
    }

    void CheckSize(int length)
    {
        if (length > maximumBytes)
        {
            throw new InvalidDataException("Save exceeds the configured size limit.");
        }
    }

    static void ValidateMetadata(SaveMetadata metadata)
    {
        if (metadata.Tick < 0 || metadata.PlayTime < TimeSpan.Zero || metadata.ContentVersions is null ||
            metadata.ContentVersions.Any(static p => string.IsNullOrWhiteSpace(p.Key) ||
                string.IsNullOrWhiteSpace(p.Value)))
        {
            throw new InvalidDataException("Save metadata is invalid.");
        }
    }

}
