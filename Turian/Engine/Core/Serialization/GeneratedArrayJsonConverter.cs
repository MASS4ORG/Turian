namespace Turian.Engine.Core;

/// <summary>Creates typed object arrays without runtime generic construction.</summary>
sealed class GeneratedArrayJsonConverter<T> : JsonConverter<T[]>
{
    /// <inheritdoc />
    public override T[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var result = new T[document.RootElement.GetArrayLength()];
        var index = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            result[index++] = GeneratedJson.Read<T>(item, options)!;
        }

        return result;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value) { GeneratedJson.Write(writer, item, options); }
        writer.WriteEndArray();
    }
}
