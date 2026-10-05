namespace Turian.Engine.Core;

/// <summary>A set of indices in one of the project's independent 32-slot layer spaces.</summary>
/// <remarks>Compatibility API for legacy consumers; use LayerMaskState for durable GUID-based selections.</remarks>
[JsonConverter(typeof(LayerMaskJsonConverter))]
public readonly record struct LayerMask(uint Value)
{
    /// <summary>Includes every layer.</summary>
    public static LayerMask Everything => new(uint.MaxValue);

    /// <summary>Includes no layers.</summary>
    public static LayerMask Nothing => default;

    /// <summary>Creates a mask containing one valid layer index.</summary>
    public static LayerMask FromLayer(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 31);
        return new(1u << index);
    }

    /// <summary>Whether this mask includes the given index; invalid indices are excluded.</summary>
    public bool Contains(int index) => (uint)index < 32 && (Value & (1u << index)) != 0;

    /// <summary>Whether the masks have any layer in common.</summary>
    public bool Intersects(LayerMask other) => (Value & other.Value) != 0;

    /// <summary>Includes layers present in both masks.</summary>
    public static LayerMask operator &(LayerMask left, LayerMask right) => new(left.Value & right.Value);

    /// <summary>Includes layers present in either mask.</summary>
    public static LayerMask operator |(LayerMask left, LayerMask right) => new(left.Value | right.Value);

    /// <summary>Includes every layer absent from the mask.</summary>
    public static LayerMask operator ~(LayerMask mask) => new(~mask.Value);

    /// <summary>Converts unsigned bits to a mask.</summary>
    public static implicit operator LayerMask(uint value) => new(value);

    /// <summary>Converts signed bits to a mask, preserving the high bit.</summary>
    public static implicit operator LayerMask(int value) => new(unchecked((uint)value));

    /// <summary>Gets the unsigned bits of a mask.</summary>
    public static implicit operator uint(LayerMask mask) => mask.Value;

    /// <summary>Gets the signed bits of a mask, preserving the high bit.</summary>
    public static implicit operator int(LayerMask mask) => unchecked((int)mask.Value);
}

/// <summary>Stores masks as numeric bits so all 32 indices round-trip without a nested object.</summary>
public sealed class LayerMaskJsonConverter : JsonConverter<LayerMask>
{
    /// <inheritdoc />
    public override LayerMask Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TryGetUInt32(out var value) ? new(value) : new(unchecked((uint)reader.GetInt32()));

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, LayerMask value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}
