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
    Func<IdObject> create,
    Action<Utf8JsonWriter, IdObject, JsonSerializerOptions> writeMembers,
    Func<IdObject, JsonProperty, JsonSerializerOptions, bool> readMember,
    Func<IdObject, string, object?, bool> setMember,
    Func<string, Type?> memberType)
{
    /// <summary>Creates an empty instance.</summary>
    public IdObject Create() => create();

    /// <summary>Writes every serialized member as JSON properties.</summary>
    /// <param name="writer">The JSON writer, inside the object.</param>
    /// <param name="value">The instance to write.</param>
    /// <param name="options">The serializer options.</param>
    public void WriteMembers(Utf8JsonWriter writer, IdObject value, JsonSerializerOptions options) =>
        writeMembers(writer, value, options);

    /// <summary>Reads one JSON property into its member.</summary>
    /// <param name="value">The instance being read.</param>
    /// <param name="property">The JSON property.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>False when no member has that name.</returns>
    public bool ReadMember(IdObject value, JsonProperty property, JsonSerializerOptions options) =>
        readMember(value, property, options);

    /// <summary>Assigns a member by name.</summary>
    /// <param name="value">The instance to write.</param>
    /// <param name="member">The member name.</param>
    /// <param name="memberValue">The value to assign.</param>
    /// <returns>False when no writable member has that name.</returns>
    public bool SetMember(IdObject value, string member, object? memberValue) => setMember(value, member, memberValue);

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
    static readonly ConcurrentDictionary<Type, GeneratedSerializer> Serializers = new();

    /// <summary>Registers the generated serializer for <paramref name="type"/>.</summary>
    /// <param name="type">The serializable type.</param>
    /// <param name="serializer">Its generated member access.</param>
    public static void Register(Type type, GeneratedSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(serializer);
        Serializers[type] = serializer;
    }

    /// <summary>Removes a registration, so the type falls back to reflection (for tests comparing the two).</summary>
    internal static bool Remove(Type type, out GeneratedSerializer? serializer) =>
        Serializers.TryRemove(type, out serializer);

    /// <summary>Forgets serializers of types from assemblies released by <see cref="CollectibleAssemblies"/>.</summary>
    internal static void ReleaseCollectible() => CollectibleAssemblies.RemoveReleased(Serializers, static type => type);

    /// <summary>Finds the generated serializer for exactly <paramref name="type"/>.</summary>
    /// <param name="type">The runtime type.</param>
    /// <param name="serializer">The serializer, when one was generated.</param>
    /// <returns>True when one was generated.</returns>
    public static bool TryGet(Type type,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GeneratedSerializer? serializer) =>
        Serializers.TryGetValue(type, out serializer);
}
