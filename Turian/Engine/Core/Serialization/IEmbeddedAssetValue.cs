namespace Turian.Engine.Core;

/// <summary>A typed inline value that participates in its owning object's GUID reference hydration.</summary>
/// <typeparam name="TSelf">The closed inline value type emitted by the source generator.</typeparam>
public interface IEmbeddedAssetValue<TSelf> where TSelf : class, IEmbeddedAssetValue<TSelf>
{
    /// <summary>Writes inline data and GUID wires using the enclosing object's pending-reference ownership.</summary>
    void WriteJson(Utf8JsonWriter writer, JsonSerializerOptions options, IdObject owner, string member);

    /// <summary>Reads inline data and registers compiled setters for deferred GUID hydration.</summary>
    static abstract TSelf ReadJson(JsonElement json, JsonSerializerOptions options, IdObject owner, string member);
}

/// <summary>Invokes compiled inline-value serialization, including explicit interface implementations and nulls.</summary>
public static class EmbeddedAssetValues
{
    /// <summary>Writes a nullable inline field without reflective serializer dispatch.</summary>
    public static void Write<T>(Utf8JsonWriter writer, T? value, JsonSerializerOptions options,
        IdObject owner, string member) where T : class, IEmbeddedAssetValue<T>
    {
        if (value is null) { writer.WriteNullValue(); }
        else { value.WriteJson(writer, options, owner, member); }
    }

    /// <summary>Reads an inline field through its closed static interface implementation.</summary>
    public static T? Read<T>(JsonElement json, JsonSerializerOptions options, IdObject owner, string member)
        where T : class, IEmbeddedAssetValue<T> =>
        json.ValueKind == JsonValueKind.Null ? null : T.ReadJson(json, options, owner, member);
}
