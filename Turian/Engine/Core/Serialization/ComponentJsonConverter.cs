namespace Turian.Engine.Core;

/// <summary>
/// JSON converter for polymorphic serialization/deserialization of Component types.
/// Uses stable <see cref="Guid"/> ids declared with <see cref="TypeIdAttribute"/> and
/// resolved through <see cref="TypeRegistry"/> so renames and namespace changes do not
/// break serialized scenes/prefabs.
/// </summary>
public class ComponentJsonConverter : JsonConverter<Component>
{
    /// <summary>
    /// Reads and converts JSON to a Component instance.
    /// </summary>
    public override Component Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty(ObjectJsonSerializer<Component>.TypeIdProperty, out var typeIdProp))
            throw new JsonException($"Missing {ObjectJsonSerializer<Component>.TypeIdProperty} on Component");

        var typeIdString = typeIdProp.GetString()
            ?? throw new JsonException($"{ObjectJsonSerializer<Component>.TypeIdProperty} is null");

        if (!Guid.TryParse(typeIdString, out var typeId))
            throw new JsonException(
                $"{ObjectJsonSerializer<Component>.TypeIdProperty} is not a valid Guid: '{typeIdString}'");

        var type = ResolveType(typeId);

        if (type is null)
        {
            var missing = new MissingComponent
            {
                UnresolvedTypeId = typeId
            };

            if (root.TryGetProperty("__OriginalTypeName", out var origProp))
            {
                missing.OriginalTypeName = origProp.GetString() ?? string.Empty;
            }

            return missing;
        }

        var instance = Activator.CreateInstance(type) as Component
            ?? throw new JsonException($"Cannot instantiate {type.FullName}");

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == ObjectJsonSerializer<Component>.TypeIdProperty) continue;
            TrySetMember(instance, type, prop, options);
        }

        return instance;
    }

    /// <summary>
    /// Writes a Component instance as JSON.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, Component value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();

        if (value is MissingComponent missing)
        {
            writer.WriteString(ObjectJsonSerializer<Component>.TypeIdProperty, missing.UnresolvedTypeId.ToString());
            if (!string.IsNullOrEmpty(missing.OriginalTypeName))
            {
                writer.WriteString("__OriginalTypeName", missing.OriginalTypeName);
            }
            writer.WriteEndObject();
            return;
        }

        var typeId = TypeRegistry.GetIdOrThrow(value.GetType());
        writer.WriteString(ObjectJsonSerializer<Component>.TypeIdProperty, typeId.ToString());

        var members = value.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Cast<MemberInfo>()
            .Concat(value.GetType()
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.GetCustomAttribute<JsonIgnoreAttribute>() is null));

        foreach (var member in members)
        {
            var (name, memberType, memberValue) = member switch
            {
                PropertyInfo p => (p.Name, p.PropertyType, p.GetValue(value)),
                FieldInfo f => (f.Name, f.FieldType, f.GetValue(value)),
                _ => default
            };
            if (name is null) continue;
            if (ObjectReferences.TryWrite(writer, value, name, memberType, memberValue, allowSceneObjects: true)) continue;
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, memberValue, memberType, options);
        }

        writer.WriteEndObject();
    }

    static Type? ResolveType(Guid typeId)
    {
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

        return null;
    }

    static void TrySetMember(Component instance, Type type, JsonProperty prop, JsonSerializerOptions options)
    {
        // Members Write skips are skipped here too, so values older files still carry for them are not applied.
        var property = type.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetCustomAttribute<JsonIgnoreAttribute>() is not null) return;
        if (property?.CanWrite == true)
        {
            if (ObjectReferences.TryRead(instance, prop.Name, property.PropertyType, prop.Value, true, out var reference))
            {
                property.SetValue(instance, reference);
                return;
            }

            property.SetValue(instance, JsonSerializer.Deserialize(prop.Value.GetRawText(), property.PropertyType, options));
            return;
        }
        var field = type.GetField(prop.Name, BindingFlags.Public | BindingFlags.Instance);
        if (field is null || field.GetCustomAttribute<JsonIgnoreAttribute>() is not null) return;
        field.SetValue(instance,
            ObjectReferences.TryRead(instance, prop.Name, field.FieldType, prop.Value, true, out var fieldReference)
                ? fieldReference
                : JsonSerializer.Deserialize(prop.Value.GetRawText(), field.FieldType, options));
    }
}
