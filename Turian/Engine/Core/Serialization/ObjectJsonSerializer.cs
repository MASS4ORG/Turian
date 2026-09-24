
namespace Turian.Engine.Core;

/// <summary>
/// Custom JSON converter for serializing and deserializing objects of type T (Object).
/// </summary>
/// <typeparam name="T">The type of objects to serialize and deserialize.</typeparam>
/// <remarks>
/// This JSON converter handles special cases, such as serializing and deserializing objects derived from Asset,
/// and allows dynamic serialization and deserialization of object properties and fields. Polymorphism is
/// handled via stable <see cref="Guid"/> ids declared with <see cref="TypeIdAttribute"/> and resolved
/// through <see cref="TypeRegistry"/>, so serialized data survives renames and namespace changes.
/// </remarks>
public class ObjectJsonSerializer<T> : JsonConverter<T>
    where T : IdClass
{
    /// <summary>
    /// Property name carrying the stable type id (a <see cref="Guid"/>) for polymorphic dispatch.
    /// </summary>
    public const string TypeIdProperty = "__TypeId";

    /// <inheritdoc/>
    public override T Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = ResolveTypeFromJson(root);
        var result = CreateInstance(type);

        ReadMembers(root, type, result, options);
        return result;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        WriteTypeInformation(writer, value);

        var members = GetCachedMembers(value.GetType());
        WriteMembers(writer, value, members, options);

        writer.WriteEndObject();
    }

    static void WriteTypeInformation(Utf8JsonWriter writer, T value)
    {
        var typeId = TypeRegistry.GetIdOrThrow(value.GetType());
        writer.WriteString(TypeIdProperty, typeId.ToString());
    }

    static void WriteMembers(
        Utf8JsonWriter writer,
        T value,
        IDictionary<string, MemberInfo> members,
        JsonSerializerOptions options
    )
    {
        foreach (var item in members)
        {
            var member = item.Value;
            if (member is PropertyInfo property)
            {
                if (property.CanRead && property.CanWrite && IsMemberValid(property))
                {
                    var propValue = property.GetValue(value);
                    WriteMemberValue(
                        writer,
                        property.Name,
                        propValue,
                        property.PropertyType,
                        options
                    );
                }
            }
            else if (member is FieldInfo field)
            {
                if (IsMemberValid(field))
                {
                    var fieldValue = field.GetValue(value);
                    WriteMemberValue(writer, field.Name, fieldValue, field.FieldType, options);
                }
            }
        }
    }

    static void WriteMemberValue(
        Utf8JsonWriter writer,
        string memberName,
        object? memberValue,
        Type memberType,
        JsonSerializerOptions options
    )
    {
        if (memberValue is Asset asset)
        {
            var assetTypeId = TypeRegistry.GetIdOrThrow(asset.GetType());
            writer.WriteStartObject(memberName);
            writer.WriteString(TypeIdProperty, assetTypeId.ToString());
            writer.WriteString("Id", asset.Id.ToString());
            writer.WriteEndObject();
        }
        else
        {
            writer.WritePropertyName(memberName);
            JsonSerializer.Serialize(writer, memberValue, memberType, options);
        }
    }

    static void ReadMembers(JsonElement root, Type type, T result, JsonSerializerOptions options)
    {
        var members = GetCachedMembers(type);

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == TypeIdProperty)
            {
                continue;
            }

            ReadMember(prop, members, result, options);
        }
    }

    static void ReadMember(
        JsonProperty prop,
        Dictionary<string, MemberInfo> members,
        T result,
        JsonSerializerOptions options
    )
    {
        if (members.TryGetValue(prop.Name, out var memberInfo))
        {
            object? value;
            Type memberType;

            if (memberInfo is PropertyInfo propertyInfo && propertyInfo.CanWrite)
            {
                memberType = propertyInfo.PropertyType;
            }
            else if (memberInfo is FieldInfo fieldInfo)
            {
                memberType = fieldInfo.FieldType;
            }
            else
            {
                return;
            }

            if (memberType == typeof(Asset) || memberType.IsSubclassOf(typeof(Asset)))
            {
                value = CreateAndSetAsset(prop);
            }
            else
            {
                value = JsonSerializer.Deserialize(prop.Value.GetRawText(), memberType, options);
            }

            if (memberInfo is PropertyInfo property)
            {
                property.SetValue(result, value);
            }
            else if (memberInfo is FieldInfo field)
            {
                field.SetValue(result, value);
            }
        }
    }

    static bool IsMemberValid(MemberInfo member)
    {
        switch (member)
        {
            case PropertyInfo property:
                var getMethod = property.GetGetMethod(nonPublic: true);
                if (getMethod == null)
                {
                    return false;
                }

                return (
                        !getMethod.IsPublic
                        && property.GetCustomAttribute(typeof(JsonIncludeAttribute)) != null
                    )
                    || (
                        getMethod.IsPublic
                        && property.GetCustomAttribute(typeof(JsonIgnoreAttribute)) == null
                    );
            case FieldInfo field:
                return (
                        !field.IsPublic
                        && field.GetCustomAttribute(typeof(JsonIncludeAttribute)) != null
                    )
                    || (
                        field.IsPublic
                        && field.GetCustomAttribute(typeof(JsonIgnoreAttribute)) == null
                    );
            default:
                throw new ArgumentException("Member is not a property or field", nameof(member));
        }
    }

    static Dictionary<string, MemberInfo> GetCachedMembers(Type type)
    {
        if (!ObjectJsonSerializerCache.Members.TryGetValue(type, out var members))
        {
            members = [];

            foreach (
                var prop in type.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                )
            )
            {
                members[prop.Name] = prop;
            }

            foreach (
                var field in type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                )
            )
            {
                members[field.Name] = field;
            }

            ObjectJsonSerializerCache.Members[type] = members;
        }
        return members;
    }

    static Type ResolveTypeFromJson(JsonElement root)
    {
        if (!root.TryGetProperty(TypeIdProperty, out var typeIdProp))
        {
            throw new JsonException($"Required property '{TypeIdProperty}' is missing.");
        }

        var typeIdString = typeIdProp.GetString()
            ?? throw new JsonException($"Property '{TypeIdProperty}' is null.");

        if (!Guid.TryParse(typeIdString, out var typeId))
        {
            throw new JsonException($"Property '{TypeIdProperty}' is not a valid Guid: '{typeIdString}'.");
        }

        if (TypeRegistry.TryGetType(typeId, out var type) && type is not null)
        {
            return type;
        }

        // Hot-reloaded user code may have registered new types after JsonOptions was built.
        TypeRegistry.ScanLoadedAssemblies();
        if (TypeRegistry.TryGetType(typeId, out type) && type is not null)
        {
            return type;
        }

        throw new UnresolvableTypeIdException(typeId,
            $"No type registered with TypeId '{typeId}'. The originating class may have been " +
            "removed or its [TypeId] attribute changed. " +
            $"This usually means the user-code assembly hasn't been compiled yet, or the type was deleted.");
    }

    static T CreateInstance(Type type)
    {
        return Activator.CreateInstance(type) as T ?? throw new JsonException("Failed to create instance");
    }

    static Asset CreateAndSetAsset(JsonProperty prop)
    {
        var actualAssetType = ResolveTypeFromJson(prop.Value);
        if (Activator.CreateInstance(actualAssetType) is not Asset asset)
        {
            throw new JsonException("Asset could not be created");
        }

        asset.Id = Guid.Parse(
            prop.Value.GetProperty("Id").GetString() ?? throw new JsonException("Id is null")
        );
        return asset;
    }
}

static class ObjectJsonSerializerCache
{
    public static readonly ConcurrentDictionary<Type, Dictionary<string, MemberInfo>> Members = new();
}
