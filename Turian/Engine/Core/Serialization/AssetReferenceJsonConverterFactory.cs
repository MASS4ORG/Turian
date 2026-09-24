namespace Turian.Engine.Core;

/// <summary>
/// Creates JSON converters for <see cref="AssetReference{TAsset}"/> and any of its subclasses
/// (such as <c>PrefabReference&lt;TComponent&gt;</c>). The declared field type is used to
/// instantiate the correct concrete reference during deserialization.
/// </summary>
public sealed class AssetReferenceJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return FindAssetReferenceAncestor(typeToConvert) is not null;
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        ArgumentNullException.ThrowIfNull(options);

        var ancestor = FindAssetReferenceAncestor(typeToConvert)
            ?? throw new InvalidOperationException(
                $"Type '{typeToConvert.FullName}' is not assignable to AssetReference<>.");

        var assetType = ancestor.GetGenericArguments()[0];
        var converterType = typeof(AssetReferenceJsonConverter<,>).MakeGenericType(typeToConvert, assetType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    static Type? FindAssetReferenceAncestor(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AssetReference<>))
            {
                return current;
            }
        }

        return null;
    }

    sealed class AssetReferenceJsonConverter<TReference, TAsset> : JsonConverter<TReference>
        where TReference : AssetReference<TAsset>
        where TAsset : Asset
    {
        public override TReference Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(typeToConvert);
            ArgumentNullException.ThrowIfNull(options);

            var assetId = ReadAssetId(ref reader);
            return CreateReference(typeToConvert, assetId);
        }

        public override void Write(
            Utf8JsonWriter writer,
            TReference value,
            JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(options);

            if (value.IsEmpty)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteString("AssetId", value.AssetId);
            writer.WriteEndObject();
        }

        static Guid ReadAssetId(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return Guid.Empty;
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                return Guid.TryParse(root.GetString(), out var parsedId) ? parsedId : Guid.Empty;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(
                    $"Expected an object, string, or null when reading AssetReference<{typeof(TAsset).Name}>.");
            }

            if (!root.TryGetProperty("AssetId", out var idElement))
            {
                return Guid.Empty;
            }

            return idElement.ValueKind switch
            {
                JsonValueKind.Null => Guid.Empty,
                JsonValueKind.String when Guid.TryParse(idElement.GetString(), out var parsedId) => parsedId,
                JsonValueKind.String => Guid.Empty,
                _ => throw new JsonException("AssetId must be a GUID string or null.")
            };
        }

        static TReference CreateReference(Type typeToConvert, Guid assetId)
        {
            if (Activator.CreateInstance(typeToConvert, assetId) is TReference reference)
            {
                return reference;
            }

            throw new JsonException(
                $"Could not construct '{typeToConvert.FullName}' from a Guid. A (Guid) constructor is required.");
        }
    }
}
