namespace Turian.Engine.Save;

/// <summary>The schema, encoding, metadata and owned payload passed between save services and container codecs.</summary>
/// <param name="SchemaId">The game's stable schema identifier.</param>
/// <param name="SchemaVersion">The game schema version.</param>
/// <param name="SerializerId">The encoding of the payload bytes.</param>
/// <param name="Metadata">Snapshot display and compatibility metadata.</param>
/// <param name="Payload">The detached encoded game state.</param>
public sealed record SaveContainer(string SchemaId, int SchemaVersion, string SerializerId,
    SaveMetadata Metadata, byte[] Payload);

/// <summary>Replaces the complete save envelope format independently of payload serializers and storage adapters.</summary>
public interface ISaveContainerCodec
{
    /// <summary>The stable identifier used to register this container format.</summary>
    string Id { get; }

    /// <summary>Recognizes this format's signature without decoding; matching formats must have distinct signatures.</summary>
    bool CanRead(ReadOnlySpan<byte> data);

    /// <summary>Encodes metadata and payload together, including the format's version and integrity protection.</summary>
    byte[] Encode(SaveContainer container);

    /// <summary>Validates integrity and format version before decoding a bounded, detached container.</summary>
    SaveContainer Decode(ReadOnlySpan<byte> data, int maximumBytes);
}
