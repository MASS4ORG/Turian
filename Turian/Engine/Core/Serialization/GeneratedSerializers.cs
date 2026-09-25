namespace Turian.Engine.Core;

/// <summary>
/// Reflection-free member access for one serializable type, emitted by the Turian source generator.
/// </summary>
/// <param name="create">Creates an empty instance.</param>
/// <param name="writeMembers">Writes every serialized member as JSON properties.</param>
/// <param name="readMember">Reads one JSON property into its member; false when no member has that name.</param>
/// <param name="setMember">Assigns a member by name; false when no writable member has that name.</param>
/// <param name="memberType">The declared type of a member by name, or null.</param>
public sealed class GeneratedSerializer(
    Func<IdClass> create,
    Action<Utf8JsonWriter, IdClass, JsonSerializerOptions> writeMembers,
    Func<IdClass, JsonProperty, JsonSerializerOptions, bool> readMember,
    Func<IdClass, string, object?, bool> setMember,
    Func<string, Type?> memberType)
{
    /// <summary>Creates an empty instance.</summary>
    public IdClass Create() => create();

    /// <summary>Writes every serialized member as JSON properties.</summary>
    /// <param name="writer">The JSON writer, inside the object.</param>
    /// <param name="value">The instance to write.</param>
    /// <param name="options">The serializer options.</param>
    public void WriteMembers(Utf8JsonWriter writer, IdClass value, JsonSerializerOptions options) =>
        writeMembers(writer, value, options);

    /// <summary>Reads one JSON property into its member.</summary>
    /// <param name="value">The instance being read.</param>
    /// <param name="property">The JSON property.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>False when no member has that name.</returns>
    public bool ReadMember(IdClass value, JsonProperty property, JsonSerializerOptions options) =>
        readMember(value, property, options);

    /// <summary>Assigns a member by name.</summary>
    /// <param name="value">The instance to write.</param>
    /// <param name="member">The member name.</param>
    /// <param name="memberValue">The value to assign.</param>
    /// <returns>False when no writable member has that name.</returns>
    public bool SetMember(IdClass value, string member, object? memberValue) => setMember(value, member, memberValue);

    /// <summary>The declared type of a member by name, or null.</summary>
    /// <param name="member">The member name.</param>
    /// <returns>The member type.</returns>
    public Type? MemberType(string member) => memberType(member);
}

/// <summary>
/// The generated serializers registered by each assembly's module initializer. A type without one is
/// serialized through reflection.
/// </summary>
public static class GeneratedSerializers
{
    static readonly ConcurrentDictionary<Type, GeneratedSerializer> serializers = new();

    /// <summary>Registers the generated serializer for <paramref name="type"/>.</summary>
    /// <param name="type">The serializable type.</param>
    /// <param name="serializer">Its generated member access.</param>
    public static void Register(Type type, GeneratedSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(serializer);
        serializers[type] = serializer;
    }

    /// <summary>Removes a registration, so the type falls back to reflection (for tests comparing the two).</summary>
    internal static bool Remove(Type type, out GeneratedSerializer? serializer) =>
        serializers.TryRemove(type, out serializer);

    /// <summary>Finds the generated serializer for exactly <paramref name="type"/>.</summary>
    /// <param name="type">The runtime type.</param>
    /// <param name="serializer">The serializer, when one was generated.</param>
    /// <returns>True when one was generated.</returns>
    public static bool TryGet(Type type,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GeneratedSerializer? serializer) =>
        serializers.TryGetValue(type, out serializer);
}
