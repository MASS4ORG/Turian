using System.Text.Json;

namespace Turian.Engine.Save;

/// <summary>The default DTO serializer; callers can supply generated type metadata through its options.</summary>
public sealed class JsonSaveSerializer : ISaveSerializer
{
    readonly JsonSerializerOptions options;

    /// <summary>Copies and freezes options so serialization can safely run on worker threads.</summary>
    public JsonSaveSerializer(JsonSerializerOptions? options = null)
    {
        this.options = options is null ? new JsonSerializerOptions() : new JsonSerializerOptions(options);
        this.options.MakeReadOnly(populateMissingResolver: true);
    }

    /// <inheritdoc />
    public string Id => "json";
    /// <inheritdoc />
    public byte[] Serialize<T>(T state) => JsonSerializer.SerializeToUtf8Bytes(state, options);
    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlySpan<byte> data) => JsonSerializer.Deserialize<T>(data, options)
        ?? throw new InvalidDataException("Save payload is null.");
}
