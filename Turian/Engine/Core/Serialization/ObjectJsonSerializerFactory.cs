namespace Turian.Engine.Core;

/// <summary>Provides TypeId serialization for concrete object types loaded after options were initialized.</summary>
sealed class ObjectJsonSerializerFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsClass && !typeToConvert.IsAbstract && typeToConvert.IsSubclassOf(typeof(IdObject));

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ObjectJsonSerializer<>).MakeGenericType(typeToConvert))!;
}
