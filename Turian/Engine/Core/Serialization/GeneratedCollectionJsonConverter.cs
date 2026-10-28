namespace Turian.Engine.Core;

/// <summary>Reads closed object collections through registered element contracts.</summary>
sealed class GeneratedCollectionJsonConverter<TCollection, T> : JsonConverter<TCollection>
    where TCollection : ICollection<T>, new()
{
    /// <inheritdoc />
    public override TCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var result = new TCollection();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            result.Add(GeneratedJson.Read<T>(item, options)!);
        }

        return result;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TCollection value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            GeneratedJson.Write(writer, item, options);
        }

        writer.WriteEndArray();
    }
}
