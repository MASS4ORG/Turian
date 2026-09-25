namespace Turian.Editor.Core;

/// <summary>
/// Discovers and filters component types that can be added to a <see cref="Node"/>
/// at edit-time. Depends only on <see cref="BuildManager"/> and engine types.
/// </summary>
public static class ComponentRegistry
{
    /// <summary>
    /// Enumerates all concrete, inspectable <see cref="Component"/> types
    /// across every assembly loaded by <see cref="BuildManager"/>.
    /// </summary>
    public static IEnumerable<Type> GetAvailableTypes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var assembly in BuildManager.Instance.LoadedAssemblies)
        {
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
                if (!IsInspectable(type)) continue;
                var key = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
                if (seen.Add(key)) yield return type;
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="type"/> is a concrete, parameterless
    /// <see cref="Component"/> subclass that is either declared outside the engine or menu-registered.
    /// </summary>
    /// <remarks>
    /// Every component a project declares is listed; the engine's own need the attribute,
    /// which keeps internal types such as <see cref="MissingComponent"/> out of the menu.
    /// </remarks>
    public static bool IsInspectable(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!typeof(Component).IsAssignableFrom(type)) return false;
        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters) return false;
        if (type.GetConstructor(Type.EmptyTypes) is null) return false;
        return !IsEngineType(type)
               || type.GetCustomAttributes(false)
                   .Any(a => a.GetType().Name == nameof(ComponentContextMenuAttribute));
    }

    static bool IsEngineType(Type type) =>
        type.Assembly.GetName().Name?.StartsWith("Turian.Engine.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="type"/> matches
    /// <paramref name="filter"/> by name, full name, or menu path.
    /// An empty filter always matches.
    /// </summary>
    public static bool MatchesSearch(Type type, string filter)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var path = GetMenuPath(type);
        return type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || type.FullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
               || path.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="componentType"/> can be
    /// added to <paramref name="node"/> (respects <c>AllowsMultiple</c>).
    /// </summary>
    public static bool CanAddTo(Node node, Type componentType)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Component.AllowsMultiple(componentType) || !node.HasComponent(componentType);
    }

    /// <summary>
    /// Returns the <see cref="ComponentContextMenuAttribute.Path"/> for
    /// <paramref name="type"/>, or the type name as fallback.
    /// </summary>
    public static string GetMenuPath(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetCustomAttribute<ComponentContextMenuAttribute>(false)?.Path ?? type.Name;
    }

    /// <summary>
    /// Returns the last segment of the menu path as a human-readable display name.
    /// </summary>
    public static string GetDisplayName(Type type)
    {
        var path = GetMenuPath(type);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length > 0 ? segments[^1] : type.Name;
    }
}
