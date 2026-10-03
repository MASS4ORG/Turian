namespace Turian.Editor.Core;

/// <summary>
/// Discovers types decorated with <see cref="NodeContextMenuAttribute"/>
/// across all assemblies loaded by <see cref="BuildManager"/>.
/// </summary>
public static class NodeContextMenuRegistry
{
    /// <summary>Returns all types in <paramref name="assemblies"/> that have a <see cref="NodeContextMenuAttribute"/>.</summary>
    /// <param name="assemblies">The assemblies to scan, usually <see cref="BuildManager.LoadedAssemblies"/>.</param>
    public static IEnumerable<Type> GetAll(IEnumerable<Assembly> assemblies) =>
        assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetCustomAttributes<NodeContextMenuAttribute>(false).Any())
            .OrderBy(t => t.Name);
}
