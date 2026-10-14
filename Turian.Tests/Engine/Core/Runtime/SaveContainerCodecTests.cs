using Turian.Engine.Save;

namespace Turian.Tests;

/// <summary>Verifies container plugins, legacy compatibility and integrity without changing game save workflows.</summary>
public sealed class SaveContainerCodecTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static readonly SaveSlot Slot = new("container");
    sealed record State(int Gold);

    sealed class TextSerializer : ISaveSerializer
    {
        public string Id => "test-text";
        public byte[] Serialize<T>(T state) => Encoding.UTF8.GetBytes(((State)(object)state!).Gold.ToString());
        public T Deserialize<T>(ReadOnlySpan<byte> data) => (T)(object)new State(int.Parse(Encoding.UTF8.GetString(data)));
    }

    sealed class BinaryContainer : ISaveContainerCodec
    {
        public string Id => "test-binary-v1";
        public bool CanRead(ReadOnlySpan<byte> data) => data.StartsWith("TSC1"u8);

        public byte[] Encode(SaveContainer container)
        {
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            writer.Write("TSC1"u8);
            writer.Write(container.SchemaId);
            writer.Write(container.SchemaVersion);
            writer.Write(container.SerializerId);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(container.Metadata);
            writer.Write(metadata.Length);
            writer.Write(metadata);
            writer.Write(container.Payload.Length);
            writer.Write(container.Payload);
            writer.Flush();
            var body = output.ToArray();
            writer.Write(SHA256.HashData(body));
            writer.Flush();
            return output.ToArray();
        }

        public SaveContainer Decode(ReadOnlySpan<byte> data, int maximumBytes)
        {
            if (data.Length > maximumBytes || data.Length < 36 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(data[..^32]), data[^32..]))
            {
                throw new InvalidDataException("Invalid binary container integrity or size.");
            }
            using var input = new MemoryStream(data[..^32].ToArray());
            using var reader = new BinaryReader(input);
            input.Position = 4;
            var schema = reader.ReadString();
            var version = reader.ReadInt32();
            var serializer = reader.ReadString();
            var metadata = JsonSerializer.Deserialize<SaveMetadata>(ReadBlock(reader))!;
            var payload = ReadBlock(reader);
            return new SaveContainer(schema, version, serializer, metadata, payload);
        }

        static byte[] ReadBlock(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid binary block length.");
            }
            return reader.ReadBytes(length);
        }
    }

    sealed class TaggedJsonContainer : ISaveContainerCodec
    {
        readonly JsonSaveContainerCodec inner = new();
        public string Id => "test-tagged-json";
        public bool CanRead(ReadOnlySpan<byte> data) => data.StartsWith("{\"Container\":\"tagged\""u8);
        public byte[] Encode(SaveContainer container) =>
            JsonSerializer.SerializeToUtf8Bytes(new { Container = "tagged", Data = inner.Encode(container) });
        public SaveContainer Decode(ReadOnlySpan<byte> data, int maximumBytes)
        {
            using var json = JsonDocument.Parse(data.ToArray());
            return inner.Decode(json.RootElement.GetProperty("Data").GetBytesFromBase64(), maximumBytes);
        }
    }

    /// <summary>Registered JSON containers are recognized before the broad legacy JSON signature.</summary>
    [Fact]
    public async Task CustomJsonContainer_RemainsReadableWhenTheWriterChanges()
    {
        var storage = new MemorySaveStorage();
        var tagged = new TaggedJsonContainer();
        var custom = new SaveService(storage, "campaign", containerCodec: tagged);
        await custom.SaveAsync(Slot, new State(31), new SaveMetadata(), cancellationToken: Token);
        var next = new SaveService(storage, "campaign", containerCodec: null, containerReaders: [tagged]);
        Assert.Equal(31, (await next.LoadAsync<State>(Slot, cancellationToken: Token))!.State.Gold);
    }

    /// <summary>A binary envelope and custom payload serializer work together without a JSON envelope.</summary>
    [Fact]
    public async Task ContainerAndSerializerPlugins_ComposeIndependently()
    {
        var storage = new MemorySaveStorage();
        var binary = new BinaryContainer();
        var serializer = new TextSerializer();
        var saves = new SaveService(storage, "campaign", writer: serializer, containerCodec: binary);
        var revision = await saves.SaveAsync(Slot, new State(73), new SaveMetadata { Tick = 12 },
            cancellationToken: Token);
        var stored = (await storage.ReadAsync(Slot, Token))!;
        Assert.True(stored.Data.AsSpan().StartsWith("TSC1"u8));
        Assert.DoesNotContain("\"Version\"", Encoding.UTF8.GetString(stored.Data), StringComparison.Ordinal);
        Assert.Equal("73", Encoding.UTF8.GetString(binary.Decode(stored.Data, 4096).Payload));
        var loaded = (await saves.LoadAsync<State>(Slot, cancellationToken: Token))!;
        Assert.Equal(73, loaded.State.Gold);
        Assert.Equal(12, loaded.Metadata.Tick);
        Assert.Equal(revision, loaded.Revision);
        Assert.Equal(12, (await saves.InspectAsync(Slot, Token))!.Metadata.Tick);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SaveService(storage, "campaign").LoadAsync<State>(Slot, cancellationToken: Token));

        var next = new SaveService(storage, "campaign", containerCodec: null,
            readers: [serializer], containerReaders: [binary]);
        Assert.Equal(73, (await next.LoadAsync<State>(Slot, cancellationToken: Token))!.State.Gold);
        await next.SaveAsync(Slot, new State(80), loaded.Metadata, revision, cancellationToken: Token);
        Assert.Equal(73, (await next.LoadBackupAsync<State>(Slot, cancellationToken: Token))!.State.Gold);
        Assert.Equal(80, (await next.LoadAsync<State>(Slot, cancellationToken: Token))!.State.Gold);
    }

    /// <summary>New container writers retain the original envelope format as a reader.</summary>
    [Fact]
    public async Task LegacyJsonEnvelope_RemainsReadable()
    {
        var storage = new MemorySaveStorage();
        var body = """
            {"SchemaId":"campaign","SchemaVersion":1,"SerializerId":"json","Metadata":{},"Payload":"eyJHb2xkIjo0NH0="}
            """u8.ToArray();
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Body = body, Checksum = SHA256.HashData(body) });
        var revision = await storage.WriteAsync(Slot, legacy, null, Token);
        var saves = new SaveService(storage, "campaign", containerCodec: new BinaryContainer());
        Assert.Equal(44, (await saves.LoadAsync<State>(Slot, cancellationToken: Token))!.State.Gold);
        await saves.SaveAsync(Slot, new State(45), new SaveMetadata(), revision, cancellationToken: Token);
        Assert.Equal(44, (await saves.LoadBackupAsync<State>(Slot, cancellationToken: Token))!.State.Gold);
    }

    /// <summary>Recognized corrupt containers fail without falling back to another decoder.</summary>
    [Fact]
    public async Task CorruptPluginContainer_IsRejected()
    {
        var storage = new MemorySaveStorage();
        var codec = new BinaryContainer();
        var saves = new SaveService(storage, "campaign", containerCodec: codec);
        var revision = await saves.SaveAsync(Slot, new State(1), new SaveMetadata(), cancellationToken: Token);
        var stored = (await storage.ReadAsync(Slot, Token))!;
        stored.Data[^1] ^= 1;
        await storage.WriteAsync(Slot, stored.Data, revision, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => saves.LoadAsync<State>(Slot, cancellationToken: Token));
    }

    /// <summary>Valid container checksums cannot make malformed game payloads loadable.</summary>
    [Fact]
    public async Task MalformedPayload_IsRejectedAfterContainerValidation()
    {
        var storage = new MemorySaveStorage();
        var codec = new JsonSaveContainerCodec();
        var container = new SaveContainer("campaign", 1, "json", new SaveMetadata(), "{invalid"u8.ToArray());
        await storage.WriteAsync(Slot, codec.Encode(container), null, Token);
        var saves = new SaveService(storage, "campaign");
        Assert.NotNull(await saves.InspectAsync(Slot, Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => saves.LoadAsync<State>(Slot, cancellationToken: Token));
    }

    /// <summary>The built-in codec recognizes whitespace and checks size, syntax, version and integrity.</summary>
    [Fact]
    public void JsonContainer_EnforcesItsContract()
    {
        var codec = new JsonSaveContainerCodec();
        var container = new SaveContainer("campaign", 1, "json", new SaveMetadata(), "{}"u8.ToArray());
        var data = codec.Encode(container);
        Assert.Equal("json-envelope-v1", codec.Id);
        Assert.True(codec.CanRead(" \n{}"u8));
        Assert.False(codec.CanRead([]));
        Assert.False(codec.CanRead("TSC1"u8));
        Assert.Equal("campaign", codec.Decode(data, 4096).SchemaId);
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.Decode(data, 0));
        Assert.Throws<InvalidDataException>(() => codec.Decode(data, 1));
        Assert.Throws<InvalidDataException>(() => codec.Decode("{"u8, 4096));
        Assert.Throws<InvalidDataException>(() => codec.Decode("null"u8, 4096));
        Assert.Throws<InvalidDataException>(() => codec.Decode("{}"u8, 4096));
        var envelope = JsonNode.Parse(data)!;
        envelope["Version"] = 2;
        Assert.Throws<InvalidDataException>(() => codec.Decode(JsonSerializer.SerializeToUtf8Bytes(envelope), 4096));
        var nullBody = "null"u8.ToArray();
        var empty = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Body = nullBody,
            Checksum = SHA256.HashData(nullBody)
        });
        Assert.Throws<InvalidDataException>(() => codec.Decode(empty, 4096));
    }
}
