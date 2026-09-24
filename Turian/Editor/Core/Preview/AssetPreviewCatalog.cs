namespace Turian.Editor.Core;

/// <summary>
/// Discovers <see cref="AssetPreviewAttribute"/>-registered preview providers across every assembly
/// visible to the editor (engine, editor and user assemblies) and resolves a provider for an asset
/// type by walking up its base types — the nearest registered ancestor wins.
///
/// <para>Providers are cached instances; the catalog refreshes automatically when the set of loaded
/// assemblies changes (e.g. after compiling user code).</para>
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class AssetPreviewCatalog
{
    readonly BuildManager buildManager;

    readonly Dictionary<string, Assembly> knownAssemblies = new(StringComparer.Ordinal);
    readonly Dictionary<Type, Type> registrations = new();
    readonly Dictionary<Type, IAssetPreviewProvider> providerCache = new();

    /// <summary>
    /// Creates a new catalog.
    /// </summary>
    /// <param name="buildManager">The build manager providing the loaded assembly set.</param>
    public AssetPreviewCatalog(BuildManager buildManager)
    {
        this.buildManager = buildManager;
        Refresh();
    }

    /// <summary>
    /// Re-scans loaded assemblies when their set changed since the last refresh. Called before
    /// resolving a provider; cheap when nothing changed.
    /// </summary>
    public void EnsureRefreshed()
    {
        var current = buildManager.LoadedAssemblies.ToList();
        if (current.Count == knownAssemblies.Count &&
            current.All(a => knownAssemblies.ContainsKey(a.FullName ?? a.GetName().Name!)))
        {
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Returns the preview provider registered for <paramref name="assetType"/>, walking up its base
    /// types when no exact match is registered. Returns <c>null</c> when nothing matches.
    /// </summary>
    public IAssetPreviewProvider? GetProvider(Type assetType)
    {
        if (assetType is null) throw new ArgumentNullException(nameof(assetType));
        EnsureRefreshed();

        for (var type = assetType; type is not null; type = type.BaseType)
        {
            if (!registrations.TryGetValue(type, out var providerType)) continue;

            if (providerCache.TryGetValue(providerType, out var cached)) return cached;

            if (Activator.CreateInstance(providerType) is not IAssetPreviewProvider provider) continue;

            providerCache[providerType] = provider;
            return provider;
        }

        return null;
    }

    void Refresh()
    {
        knownAssemblies.Clear();
        registrations.Clear();
        providerCache.Clear();

        foreach (var assembly in buildManager.LoadedAssemblies)
        {
            knownAssemblies[assembly.FullName ?? assembly.GetName().Name ?? string.Empty] = assembly;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(t => t is not null).Cast<Type>()];
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type is null || !typeof(IAssetPreviewProvider).IsAssignableFrom(type) || type.IsAbstract ||
                    type.IsInterface || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                var attribute = type.GetCustomAttribute<AssetPreviewAttribute>();
                if (attribute is null) continue;

                registrations[attribute.AssetType] = type;
            }
        }
    }
}
