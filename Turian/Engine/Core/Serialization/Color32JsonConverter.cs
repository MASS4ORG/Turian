namespace Turian.Engine.Core;

/// <summary>
/// (De)serializes <see cref="Color32"/> as <c>"#RRGGBBAA"</c> rather than its packed <see cref="uint"/>
/// field, which is <c>readonly</c> and has no settable member STJ can bind to — without this
/// converter, a <see cref="Color32"/> round-trips as transparent black. A hex string is also
/// reviewable in a scene-file diff, unlike an opaque packed integer.
/// </summary>
public sealed class Color32JsonConverter : JsonConverter<Color32>
{
    /// <inheritdoc />
    public override Color32 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var hex = reader.GetString()
            ?? throw new JsonException("Expected a \"#RRGGBBAA\" color string.");
        return Color32.FromHex(hex);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Color32 value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToHex());
    }
}
