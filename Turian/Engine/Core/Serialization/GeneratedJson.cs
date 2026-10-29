using System.Text.Json.Serialization.Metadata;

namespace Turian.Engine.Core;

/// <summary>Closed JSON contracts registered by generated code, without reflective converter construction.</summary>
public static class GeneratedJson
{
    static readonly ConcurrentDictionary<Type, Func<JsonSerializerOptions, JsonTypeInfo>> Contracts = new();
    static Func<JsonSerializerOptions, IJsonTypeInfoResolver>? valueContracts;

    /// <summary>Supplies exported value contracts before assets load; null restores the default resolver chain.</summary>
    public static void Configure(Func<JsonSerializerOptions, IJsonTypeInfoResolver>? create)
    {
        valueContracts = create;
        Serializer.ClearOptions();
    }

    internal static IJsonTypeInfoResolver CreateResolver(JsonSerializerOptions options)
    {
        var resolvers = new List<IJsonTypeInfoResolver> { new Resolver(), AssetCatalogJsonContext.Default };
        if (valueContracts is not null)
        {
            resolvers.Add(valueContracts(new JsonSerializerOptions(options)));
        }

        if (JsonSerializer.IsReflectionEnabledByDefault)
        {
            resolvers.Add(new DefaultJsonTypeInfoResolver());
        }

        return JsonTypeInfoResolver.Combine([.. resolvers]);
    }

    /// <summary>Registers the closed converter for a serialized type before options are first used.</summary>
    public static void Register<T>(JsonConverter<T> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        Contracts[typeof(T)] = options => JsonMetadataServices.CreateValueInfo<T>(options, converter);
    }

    /// <summary>Registers a closed collection converter without constructing a generic type at runtime.</summary>
    public static void RegisterCollection<TCollection, T>() where TCollection : ICollection<T>, new() =>
        Register(new GeneratedCollectionJsonConverter<TCollection, T>());

    /// <summary>Registers a closed polymorphic object and its reference collections, including abstract bases.</summary>
    public static void RegisterObject<T>() where T : IdObject
    {
        Register(new ObjectJsonSerializer<T>());
        RegisterCollection<List<T>, T>();
        Register(new GeneratedArrayJsonConverter<T>());
        ObjectReferences.RegisterCollections<T>();
    }

    /// <summary>Writes a value using an explicit JSON contract.</summary>
    public static void Write<T>(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));

    /// <summary>Reads a value using an explicit JSON contract.</summary>
    public static T? Read<T>(JsonElement value, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(value, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));

    /// <summary>Forgets closed contracts from unloaded user assemblies.</summary>
    internal static void ReleaseCollectible()
    {
        CollectibleAssemblies.RemoveReleased(Contracts, static type => type);
        if (valueContracts?.Method.DeclaringType?.Assembly is { } assembly && CollectibleAssemblies.IsReleased(assembly))
        {
            valueContracts = null;
        }
    }

    internal sealed class Resolver : IJsonTypeInfoResolver
    {
        /// <inheritdoc />
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            Contracts.TryGetValue(type, out var create) ? create(options) : null;
    }
}
