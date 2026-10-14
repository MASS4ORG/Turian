using System.Security.Cryptography;
using System.Text.Json;

namespace Turian.Engine.Save;

/// <summary>The version-one JSON envelope with SHA-256 integrity validation and Base64 payload bytes.</summary>
public sealed class JsonSaveContainerCodec : ISaveContainerCodec
{
    const int Version = 1;

    /// <inheritdoc />
    public string Id => "json-envelope-v1";

    /// <inheritdoc />
    public bool CanRead(ReadOnlySpan<byte> data)
    {
        data = data.Trim(" \t\r\n"u8);
        return !data.IsEmpty && data[0] == (byte)'{';
    }

    /// <inheritdoc />
    public byte[] Encode(SaveContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var body = JsonSerializer.SerializeToUtf8Bytes(container);
        return JsonSerializer.SerializeToUtf8Bytes(new Envelope(Version, body, SHA256.HashData(body)));
    }

    /// <inheritdoc />
    public SaveContainer Decode(ReadOnlySpan<byte> data, int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (data.Length > maximumBytes)
        {
            throw new InvalidDataException("Save exceeds the container size limit.");
        }
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(data)
                ?? throw new InvalidDataException("Missing save envelope.");
            Validate(envelope);
            return JsonSerializer.Deserialize<SaveContainer>(envelope.Body)
                ?? throw new InvalidDataException("Missing save body.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid save container JSON.", error);
        }
    }

    static void Validate(Envelope envelope)
    {
        if (envelope.Version != Version || envelope.Body is null || envelope.Checksum is null ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(envelope.Body), envelope.Checksum))
        {
            throw new InvalidDataException("Save envelope version or checksum is invalid.");
        }
    }

    sealed record Envelope(int Version, byte[] Body, byte[] Checksum);
}
