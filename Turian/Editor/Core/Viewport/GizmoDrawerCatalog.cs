namespace Turian.Editor.Core;

/// <summary>
/// Discovers <see cref="CustomGizmoAttribute"/>-registered gizmo drawers across every assembly
/// visible to the editor (engine, editor and user assemblies) and resolves drawers for component types.
///
/// <para>Drawers are cached instances; the catalog refreshes automatically when the set of loaded
/// assemblies changes (e.g. after compiling user code).</para>
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class GizmoDrawerCatalog
{
    readonly BuildManager buildManager;

    readonly Dictionary<string, Assembly> knownAssemblies = new(StringComparer.Ordinal);
    readonly Dictionary<Type, List<Type>> registrations = new();
    readonly Dictionary<Type, IGizmoDrawer> drawerCache = new();

    /// <summary>
    /// Creates a new catalog.
    /// </summary>
    /// <param name="buildManager">The build manager providing the loaded assembly set.</param>
    public GizmoDrawerCatalog(BuildManager buildManager)
    {
        this.buildManager = buildManager;
        Refresh();
    }

    /// <summary>
    /// Re-scans loaded assemblies when their set changed since the last refresh. Called before
    /// resolving drawers; cheap when nothing changed.
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
    /// Returns the drawers registered for <paramref name="componentType"/> (including drawers whose
    /// target type is a base type of it). Returns an empty sequence when none are registered.
    /// </summary>
    public IEnumerable<IGizmoDrawer> GetDrawers(Type componentType)
    {
        if (componentType is null) throw new ArgumentNullException(nameof(componentType));
        EnsureRefreshed();

        foreach (var (targetType, drawerTypes) in registrations)
        {
            if (!targetType.IsAssignableFrom(componentType)) continue;
            foreach (var drawerType in drawerTypes)
            {
                if (drawerCache.TryGetValue(drawerType, out var cached))
                {
                    yield return cached;
                    continue;
                }

                if (Activator.CreateInstance(drawerType) is IGizmoDrawer drawer)
                {
                    drawerCache[drawerType] = drawer;
                    yield return drawer;
                }
            }
        }
    }

    void Refresh()
    {
        knownAssemblies.Clear();
        registrations.Clear();
        drawerCache.Clear();

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
                if (!typeof(IGizmoDrawer).IsAssignableFrom(type) || type.IsAbstract ||
                    type.IsInterface || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                foreach (var attribute in type.GetCustomAttributes<CustomGizmoAttribute>())
                {
                    if (attribute.DrawerType != type) continue;
                    if (!registrations.TryGetValue(attribute.ComponentType, out var list))
                    {
                        list = [];
                        registrations[attribute.ComponentType] = list;
                    }

                    list.Add(type);
                }
            }
        }
    }
}
