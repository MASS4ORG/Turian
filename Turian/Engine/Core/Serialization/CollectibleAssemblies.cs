namespace Turian.Engine.Core;

/// <summary>
/// Releases the engine's static type caches for collectible assemblies that are about to unload, so their
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> can be collected.
/// </summary>
/// <remarks>
/// An unloading assembly stays in <see cref="AppDomain.GetAssemblies"/> until it is collected; released assemblies
/// are remembered weakly so assembly scans skip them instead of caching their types again.
/// </remarks>
public static class CollectibleAssemblies
{
    static readonly ConditionalWeakTable<Assembly, object> Released = [];
    static readonly object Marker = new();

    /// <summary>Forgets every cached type that comes from <paramref name="assemblies"/>; call before unloading them.</summary>
    /// <param name="assemblies">The assemblies of the load context being unloaded.</param>
    public static void Release(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies)
            Released.AddOrUpdate(assembly, Marker);

        TypeRegistry.ReleaseCollectible();
        GeneratedSerializers.ReleaseCollectible();
        ObjectReferences.ReleaseCollectible();
        RemoveReleased(ObjectJsonSerializerCache.Members, static type => type);
        SceneServiceInjector.ReleaseCollectible();
        Serializer.ClearOptions();
    }

    /// <summary>Whether <paramref name="assembly"/> was released and must not be cached again.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <returns>True for a released assembly.</returns>
    public static bool IsReleased(Assembly assembly) => Released.TryGetValue(assembly, out _);

    /// <summary>Whether <paramref name="type"/>, its element type or a generic argument comes from a released assembly.</summary>
    /// <param name="type">The type.</param>
    /// <returns>True when caching the type would keep a released assembly alive.</returns>
    public static bool IsReleased(Type type) =>
        type.HasElementType
            ? IsReleased(type.GetElementType()!)
            : IsReleased(type.Assembly) || type.IsConstructedGenericType && type.GenericTypeArguments.Any(IsReleased);

    /// <summary>Removes the entries of a type-keyed cache whose type comes from a released assembly.</summary>
    /// <param name="cache">The cache.</param>
    /// <param name="typeOf">The type an entry's key holds.</param>
    public static void RemoveReleased<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache, Func<TKey, Type?> typeOf)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(typeOf);
        foreach (var key in cache.Keys)
            if (typeOf(key) is { } type && IsReleased(type))
                cache.TryRemove(key, out _);
    }
}
