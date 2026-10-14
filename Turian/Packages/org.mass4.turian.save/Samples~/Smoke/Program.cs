using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Turian.Engine.Save;

if (args is ["--crash-writer", var crashRoot])
{
    var storage = new FileSaveStorage(crashRoot);
    var slot = new SaveSlot("interrupted");
    var previous = (await storage.ReadAsync(slot))!;
    var payload = new byte[96 * 1024 * 1024];
    Array.Fill(payload, (byte)8);
    await storage.WriteAsync(slot, payload, previous.Revision);
    return;
}

var root = Path.Combine(Path.GetTempPath(), "turian-save-demo-" + Guid.NewGuid().ToString("N"));
try
{
    await LocalSlots();
    await InterruptedProcess();
    await SchemaUpgrade();
    await SerializerPlugins();
    await CloudConflict();
    await ServerReplay();
    Console.WriteLine("PASS: all save examples");
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, true);
    }
}

async Task LocalSlots()
{
    var storage = new FileSaveStorage(root);
    var saves = new SaveService(storage, "example:campaign");
    var slot = new SaveSlot("campaign-1");
    var first = await saves.SaveAsync(slot, new Campaign(10), new SaveMetadata { GameVersion = "1.0" });
    await saves.SaveAsync(slot, new Campaign(20), new SaveMetadata { GameVersion = "1.1" }, first);
    Require((await saves.LoadAsync<Campaign>(slot))!.State.Gold == 20, "local state");
    Require((await saves.LoadBackupAsync<Campaign>(slot))!.State.Gold == 10, "backup state");
    Require((await storage.ListAsync()).Count == 1, "slot listing");
    Require((await saves.InspectAsync(slot))!.Metadata.GameVersion == "1.1", "menu metadata");
    Console.WriteLine("PASS: local atomic slots and previous-generation recovery");
}

async Task InterruptedProcess()
{
    var directory = Path.Combine(root, "interruption");
    var storage = new FileSaveStorage(directory);
    var slot = new SaveSlot("interrupted");
    await storage.WriteAsync(slot, new byte[] { 7 }, null);
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
    {
        start.ArgumentList.Add(typeof(Campaign).Assembly.Location);
    }
    start.ArgumentList.Add("--crash-writer");
    start.ArgumentList.Add(directory);
    using var child = Process.Start(start)!;
    var deadline = Stopwatch.StartNew();
    var observedTemporary = false;
    while (!child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(10))
    {
        if (Directory.EnumerateFiles(directory, "*.tmp").Any())
        {
            observedTemporary = true;
            child.Kill(entireProcessTree: true);
            break;
        }
        await Task.Delay(1);
    }
    if (!child.HasExited)
    {
        child.Kill(entireProcessTree: true);
    }
    await child.WaitForExitAsync();
    Require(observedTemporary, "interruption during real storage write");
    var committed = (await new FileSaveStorage(directory).ReadAsync(slot))!;
    Require(committed.Data is [7] ||
        committed.Data.Length == 96 * 1024 * 1024 && committed.Data.All(static b => b == 8), "no partial commit");
    Console.WriteLine("PASS: killing a writer during a real temporary-file write leaves a complete committed generation");
}

static async Task SchemaUpgrade()
{
    var storage = new MemorySaveStorage();
    var slot = new SaveSlot("legacy");
    await new SaveService(storage, "example:campaign").SaveAsync(slot, new LegacyCampaign(42), new SaveMetadata());
    var next = new SaveService(storage, "example:campaign", 2, migrations: [new GoldMigration()]);
    Require((await next.LoadAsync<Campaign>(slot))!.State.Gold == 42, "schema upgrade");
    Console.WriteLine("PASS: v1 Score upgrades to v2 Gold without rewriting the old save");
}

static async Task SerializerPlugins()
{
    var storage = new MemorySaveStorage();
    var slot = new SaveSlot("encoded");
    var compressed = new BrotliJsonSerializer();
    var saves = new SaveService(storage, "example:campaign", writer: compressed);
    var revision = await saves.SaveAsync(slot, new Campaign(99), new SaveMetadata());
    var jsonWriter = new SaveService(storage, "example:campaign", readers: [compressed]);
    Require((await jsonWriter.LoadAsync<Campaign>(slot))!.State.Gold == 99, "old serializer reader");
    using var encrypted = new AesGcmSaveSerializer(new JsonSaveSerializer(), RandomNumberGenerator.GetBytes(32));
    var secure = new SaveService(storage, "example:campaign", writer: encrypted);
    await secure.SaveAsync(slot, new Campaign(123), new SaveMetadata(), revision);
    Require((await secure.LoadAsync<Campaign>(slot))!.State.Gold == 123, "encrypted payload");
    Console.WriteLine("PASS: serializer plugin, legacy encoding reader and AES-GCM encryption");
}

static async Task CloudConflict()
{
    var device = new MemorySaveStorage();
    var cloud = new MemorySaveStorage();
    var slot = new SaveSlot("campaign");
    var local = new SaveService(device, "example:campaign");
    var remote = new SaveService(cloud, "example:campaign");
    var first = await local.SaveAsync(slot, new Campaign(10), new SaveMetadata());
    var synced = await SaveTransfer.CopyAsync(device, cloud, slot, first, null);
    var offline = await local.SaveAsync(slot, new Campaign(20), new SaveMetadata(), first);
    await remote.SaveAsync(slot, new Campaign(30), new SaveMetadata(), synced);
    var conflicted = false;
    try
    {
        await SaveTransfer.CopyAsync(device, cloud, slot, offline, synced);
    }
    catch (SaveConflictException)
    {
        conflicted = true;
    }
    Require(conflicted, "cloud conflict detection");
    Require((await local.LoadAsync<Campaign>(slot))!.State.Gold == 20, "offline branch retained");
    var chosen = (await remote.LoadAsync<Campaign>(slot))!;
    await SaveTransfer.CopyAsync(cloud, device, slot, chosen.Revision, offline);
    Require((await local.LoadAsync<Campaign>(slot))!.State.Gold == 30, "explicit player choice");
    Console.WriteLine("PASS: offline and remote edits conflict; explicit choice pulls the remote generation");
}

static async Task ServerReplay()
{
    var timer = Stopwatch.StartNew();
    var target = Guid.NewGuid();
    var entities = Enumerable.Range(0, 10000)
        .Select(i => new EntityState(i == 0 ? target : Guid.NewGuid(), target, i)).ToArray();
    var snapshot = new WorldState(150, 67890, entities);
    var captureTime = timer.Elapsed.TotalMilliseconds;
    var storage = new MemorySaveStorage();
    var saves = new SaveService(storage, "example:server-world");
    var slot = new SaveSlot("world");
    var metadata = new SaveMetadata
    {
        Tick = snapshot.Tick,
        RngState = snapshot.RngState,
        ContentVersions = new Dictionary<string, string> { ["base"] = "v1", ["example.mod"] = "v2" }
    };
    timer.Restart();
    var pending = saves.SaveAsync(slot, snapshot, metadata);
    var queueTime = timer.Elapsed.TotalMilliseconds;
    await pending;
    var loaded = (await saves.LoadAsync<WorldState>(slot, m =>
        Require(m.ContentVersions["base"] == "v1" && m.ContentVersions["example.mod"] == "v2", "content compatibility")))!;
    var byId = loaded.State.Entities.ToDictionary(static e => e.Id);
    Require(loaded.State.Entities.All(e => byId.ContainsKey(e.TargetId)), "stable reference resolution");
    Require(Advance(snapshot) == Advance(loaded.State), "deterministic replay");
    Console.WriteLine($"PASS: authoritative replay of 10,000 entities; capture {captureTime:F2} ms, queue {queueTime:F2} ms");
}

static string Advance(WorldState world)
{
    var rng = world.RngState;
    var entities = world.Entities.ToArray();
    for (var i = 0; i < entities.Length; i++)
    {
        rng ^= rng << 13;
        rng ^= rng >> 7;
        rng ^= rng << 17;
        entities[i] = entities[i] with { Health = entities[i].Health + (int)(rng % 7) };
    }
    return JsonSerializer.Serialize(new WorldState(world.Tick + 1, rng, entities));
}

static void Require(bool condition, string feature)
{
    if (!condition)
    {
        throw new InvalidOperationException("Failed: " + feature);
    }
}

sealed record Campaign(int Gold);
sealed record LegacyCampaign(int Score);
sealed record EntityState(Guid Id, Guid TargetId, int Health);
sealed record WorldState(long Tick, ulong RngState, EntityState[] Entities);

sealed class GoldMigration : ISaveMigration
{
    public int FromVersion => 1;
    public string SerializerId => "json";
    public byte[] Upgrade(ReadOnlyMemory<byte> data)
    {
        var state = JsonNode.Parse(data.Span)!.AsObject();
        state["Gold"] = state["Score"]!.DeepClone();
        state.Remove("Score");
        return JsonSerializer.SerializeToUtf8Bytes(state);
    }
}

sealed class BrotliJsonSerializer : ISaveSerializer
{
    readonly JsonSaveSerializer json = new();
    public string Id => "example:brotli-json-v1";
    public byte[] Serialize<T>(T state)
    {
        using var output = new MemoryStream();
        using (var stream = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            stream.Write(json.Serialize(state));
        }
        return output.ToArray();
    }

    public T Deserialize<T>(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var stream = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if (output.Length + read > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("Decompressed save exceeds the size limit.");
            }
            output.Write(buffer, 0, read);
        }
        return json.Deserialize<T>(output.ToArray());
    }
}
