namespace Turian.Engine.Core;

/// <summary>Closed constructors for runtime assets whose metadata may not have serialized members.</summary>
public static class GeneratedObjectFactory
{
    static readonly ConcurrentDictionary<Type, Func<IdObject>> Factories = new();

    /// <summary>Registers a statically rooted parameterless constructor.</summary>
    public static void Register<T>() where T : IdObject, new() => Factories[typeof(T)] = static () => new T();

    /// <summary>Creates an object using its generated constructor, with dynamic fallback only on JIT runtimes.</summary>
    public static IdObject? Create(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Factories.TryGetValue(type, out var create))
        {
            return create();
        }

        if (GeneratedSerializers.TryGet(type, out var serializer))
        {
            return serializer.Create();
        }

        return RuntimeFeature.IsDynamicCodeSupported ? Activator.CreateInstance(type) as IdObject
            : throw new InvalidOperationException($"No generated constructor for '{type.FullName}'.");
    }

    internal static void ReleaseCollectible() => CollectibleAssemblies.RemoveReleased(Factories, static type => type);
}
