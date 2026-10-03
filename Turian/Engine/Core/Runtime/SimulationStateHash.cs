namespace Turian.Engine.Core;

/// <summary>SHA-256 of JSON with ordinal object keys; array order and numeric representation remain significant.</summary>
public static class SimulationStateHash
{
    /// <summary>Hashes game-selected fields; duplicate property names are rejected instead of silently normalized.</summary>
    public static string Compute(JsonElement state)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, state);
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }

    static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, value);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    static void WriteObject(Utf8JsonWriter writer, JsonElement value)
    {
        writer.WriteStartObject();
        string? previous = null;
        foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (property.Name == previous) throw new InvalidDataException("State hash input contains duplicate keys.");
            previous = property.Name;
            writer.WritePropertyName(property.Name);
            Write(writer, property.Value);
        }
        writer.WriteEndObject();
    }
}
