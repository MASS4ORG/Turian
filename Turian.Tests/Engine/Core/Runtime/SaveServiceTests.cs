using Turian.Engine.Save;

namespace Turian.Tests;

/// <summary>Exercises game schema upgrades, worker isolation, integrity and optimistic cloud transfers.</summary>
public sealed class SaveServiceTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static readonly SaveSlot Slot = new("campaign-1");

    sealed record World(long Tick, ulong Rng, int[] Entities);
    sealed record Legacy(int Score);
    sealed record Current(int Gold);

    sealed class Migration : ISaveMigration
    {
        public int FromVersion => 1;
        public string SerializerId => "json";
        public byte[] Upgrade(ReadOnlyMemory<byte> data)
        {
            var root = JsonNode.Parse(data.Span)!.AsObject();
            root["Gold"] = root["Score"]!.DeepClone();
            root.Remove("Score");
            return JsonSerializer.SerializeToUtf8Bytes(root);
        }
    }

    sealed class AlternateSerializer : ISaveSerializer
    {
        public string Id => "sample-text";
        public byte[] Serialize<T>(T state) => Encoding.UTF8.GetBytes(((Current)(object)state!).Gold.ToString());
        public T Deserialize<T>(ReadOnlySpan<byte> data)
            => (T)(object)new Current(int.Parse(Encoding.UTF8.GetString(data)));
    }

    /// <summary>Schema migrations transform detached data and leave the stored generation untouched.</summary>
    [Fact]
    public async Task Migration_UpgradesGameSchemaWithoutRewritingSlot()
    {
        var storage = new MemorySaveStorage();
        var old = new SaveService(storage, "campaign");
        var revision = await old.SaveAsync(Slot, new Legacy(12), new SaveMetadata(), cancellationToken: Token);
        var next = new SaveService(storage, "campaign", 2, migrations: [new Migration()]);
        var loaded = await next.LoadAsync<Current>(Slot, cancellationToken: Token);
        Assert.Equal(12, loaded!.State.Gold);
        var info = (await next.InspectAsync(Slot, Token))!;
        Assert.Equal(1, info.SchemaVersion);
        Assert.Equal("json", info.SerializerId);
        Assert.Equal(revision, info.Revision);
        Assert.Equal(revision, loaded.Revision);
        Assert.Equal(12, (await old.LoadAsync<Legacy>(Slot, cancellationToken: Token))!.State.Score);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SaveService(storage, "campaign", 2).LoadAsync<Current>(Slot, cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SaveService(storage, "another-game").LoadAsync<Current>(Slot, cancellationToken: Token));
        await next.SaveAsync(Slot, loaded.State, loaded.Metadata, revision, cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => old.LoadAsync<Legacy>(Slot, cancellationToken: Token));
    }

    /// <summary>New writers can keep old encodings readable without converting storage during load.</summary>
    [Fact]
    public async Task Serializer_PluginsCanCoexistAcrossReleases()
    {
        var storage = new MemorySaveStorage();
        var alternate = new AlternateSerializer();
        await new SaveService(storage, "campaign", writer: alternate)
            .SaveAsync(Slot, new Current(81), new SaveMetadata(), cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SaveService(storage, "campaign").LoadAsync<Current>(Slot, cancellationToken: Token));
        var service = new SaveService(storage, "campaign", readers: [alternate]);
        var loaded = await service.LoadAsync<Current>(Slot, cancellationToken: Token);
        Assert.Equal(81, loaded!.State.Gold);
        var revision = await service.SaveAsync(Slot, loaded.State, loaded.Metadata, loaded.Revision,
            cancellationToken: Token);
        Assert.Equal(revision, (await service.LoadAsync<Current>(Slot, cancellationToken: Token))!.Revision);
    }

    /// <summary>Corruption and invalid metadata fail before decoding or replacing a valid slot.</summary>
    [Fact]
    public async Task Integrity_RejectsCorruptAndOversizedSaves()
    {
        var storage = new MemorySaveStorage();
        var service = new SaveService(storage, "campaign");
        var revision = await service.SaveAsync(Slot, new Current(12), new SaveMetadata(), cancellationToken: Token);
        var saved = (await storage.ReadAsync(Slot, cancellationToken: Token))!;
        var envelope = JsonNode.Parse(saved.Data)!;
        envelope["Checksum"] = Convert.ToBase64String(new byte[32]);
        var corrupt = await storage.WriteAsync(Slot, JsonSerializer.SerializeToUtf8Bytes(envelope), revision,
            cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync<Current>(Slot,
            cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectAsync(Slot, Token));
        Assert.Equal(12, (await service.LoadBackupAsync<Current>(Slot, cancellationToken: Token))!.State.Gold);
        await storage.WriteAsync(Slot, "{"u8.ToArray(), corrupt, cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectAsync(Slot, Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync<Current>(Slot,
            cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SaveAsync(Slot, new Current(0), new SaveMetadata { Tick = -1 }, cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SaveAsync(Slot, new Current(0), new SaveMetadata { PlayTime = TimeSpan.FromSeconds(-1) },
                cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SaveAsync(Slot, new Current(0), new SaveMetadata
            {
                ContentVersions = new Dictionary<string, string> { ["base"] = "" }
            }, cancellationToken: Token));
        var tiny = new SaveService(storage, "campaign", maximumBytes: 1);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            tiny.SaveAsync(new SaveSlot("tiny"), new Current(0), new SaveMetadata(), cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => tiny.LoadAsync<Current>(Slot, cancellationToken: Token));
    }

    /// <summary>Cloud sync refuses stale local and remote observations and preserves both device branches.</summary>
    [Fact]
    public async Task Cloud_RequiresObservedRevisionsOnBothSides()
    {
        var device = new MemorySaveStorage();
        var cloud = new MemorySaveStorage();
        var local = new SaveService(device, "campaign");
        var remote = new SaveService(cloud, "campaign");
        var first = await local.SaveAsync(Slot, new Current(10), new SaveMetadata(), cancellationToken: Token);
        var cloudRevision = await SaveTransfer.CopyAsync(device, cloud, Slot, first, null, cancellationToken: Token);
        var localRevision = await local.SaveAsync(Slot, new Current(20), new SaveMetadata(), first,
            cancellationToken: Token);
        var changedCloud = await remote.SaveAsync(Slot, new Current(30), new SaveMetadata(), cloudRevision,
            cancellationToken: Token);
        await Assert.ThrowsAsync<SaveConflictException>(() =>
            SaveTransfer.CopyAsync(device, cloud, Slot, localRevision, cloudRevision, cancellationToken: Token));
        await Assert.ThrowsAsync<SaveConflictException>(() =>
            SaveTransfer.CopyAsync(device, cloud, Slot, first, changedCloud, cancellationToken: Token));
        Assert.Equal(20, (await local.LoadAsync<Current>(Slot, cancellationToken: Token))!.State.Gold);
        Assert.Equal(30, (await remote.LoadAsync<Current>(Slot, cancellationToken: Token))!.State.Gold);
        await SaveTransfer.CopyAsync(cloud, device, Slot, changedCloud, localRevision, cancellationToken: Token);
        Assert.Equal(30, (await local.LoadAsync<Current>(Slot, cancellationToken: Token))!.State.Gold);
    }

    /// <summary>A detached 10,000-entity server snapshot resumes the same RNG sequence and entity results.</summary>
    [Fact]
    public async Task Replay_ResumesTenThousandEntitiesDeterministically()
    {
        var storage = new MemorySaveStorage();
        var service = new SaveService(storage, "server-world");
        var world = new World(72, 12345, [.. Enumerable.Range(0, 10000)]);
        var owned = world with { Entities = [.. world.Entities] };
        var pending = service.SaveAsync(Slot, owned, new SaveMetadata
        {
            Tick = world.Tick,
            RngState = world.Rng,
            ContentVersions = new Dictionary<string, string> { ["base"] = "v1", ["mod.example"] = "v2" }
        }, cancellationToken: Token);
        world.Entities[0] = -1;
        await pending;
        var loaded = (await service.LoadAsync<World>(Slot, metadata =>
        {
            if (metadata.ContentVersions["base"] != "v1")
            {
                throw new InvalidDataException("Incompatible base content.");
            }
        }, cancellationToken: Token))!;
        Assert.Equal(0, loaded.State.Entities[0]);
        Assert.Equal(72, loaded.Metadata.Tick);
        Assert.Equal(owned.Entities, loaded.State.Entities);
        Assert.Equal(Step(owned), Step(loaded.State));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync<World>(Slot,
            _ => throw new InvalidDataException("mod mismatch"), cancellationToken: Token));
    }

    static string Step(World world)
    {
        var rng = world.Rng;
        int[] entities = [.. world.Entities];
        for (var i = 0; i < entities.Length; i++)
        {
            rng ^= rng << 13;
            rng ^= rng >> 7;
            rng ^= rng << 17;
            entities[i] += (int)(rng % 10);
        }
        return JsonSerializer.Serialize(new World(world.Tick + 1, rng, entities));
    }

    /// <summary>Encryption uses unique nonces and rejects wrong keys, tampering and truncated ciphertext.</summary>
    [Fact]
    public void Encryption_AuthenticatesPayloadAndClearsDisposedKeys()
    {
        var key = new byte[32];
        using var codec = new AesGcmSaveSerializer(new JsonSaveSerializer(), key);
        key[0] = 7;
        var first = codec.Serialize(new Current(44));
        var second = codec.Serialize(new Current(44));
        Assert.NotEqual(first, second);
        Assert.Equal(44, codec.Deserialize<Current>(first).Gold);
        Assert.Equal("aes-gcm-v1:json", codec.Id);
        using var wrong = new AesGcmSaveSerializer(new JsonSaveSerializer(), key);
        Assert.Throws<InvalidDataException>(() => wrong.Deserialize<Current>(first));
        first[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => codec.Deserialize<Current>(first));
        Assert.Throws<InvalidDataException>(() => codec.Deserialize<Current>(new byte[10]));
        Assert.Throws<ArgumentException>(() => new AesGcmSaveSerializer(new JsonSaveSerializer(), new byte[5]));
        codec.Dispose();
        Assert.Throws<ObjectDisposedException>(() => codec.Serialize(new Current(1)));
        Assert.Throws<ObjectDisposedException>(() => codec.Deserialize<Current>(second));
    }

    /// <summary>Absent slots, cancellation and constructor validation are explicit.</summary>
    [Fact]
    public async Task Validation_RejectsInvalidInputsAndCancellation()
    {
        var storage = new MemorySaveStorage();
        var service = new SaveService(storage, "campaign");
        Assert.Null(await service.LoadAsync<Current>(Slot, cancellationToken: Token));
        Assert.Null(await service.InspectAsync(Slot, Token));
        Assert.Null(await service.LoadBackupAsync<Current>(Slot, cancellationToken: Token));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new SaveService(Substitute.For<ISaveStorage>(), "campaign").LoadBackupAsync<Current>(Slot,
                cancellationToken: Token));
        Assert.Throws<ArgumentException>(() => new SaveSlot("../escape"));
        Assert.Throws<ArgumentException>(() => new SaveSlot(new string('a', 65)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.LoadAsync<Current>(default,
            cancellationToken: Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaveService(storage, "campaign", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaveService(storage, "campaign", maximumBytes: 0));
        Assert.Throws<ArgumentException>(() => new SaveService(storage, ""));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SaveAsync(Slot, new Current(1), new SaveMetadata(), cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.ListAsync(cancellation.Token));
    }
}
