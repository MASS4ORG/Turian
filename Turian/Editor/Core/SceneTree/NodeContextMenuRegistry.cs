namespace Turian.Editor.Core;

/// <summary>
/// Discovers types decorated with <see cref="NodeContextMenuAttribute"/>
/// across all assemblies loaded by <see cref="BuildManager"/>.
/// </summary>
public static class NodeContextMenuRegistry
{
    /// <summary>Returns all types that have a <see cref="NodeContextMenuAttribute"/>.</summary>
    public static IEnumerable<Type> GetAll() =>
        BuildManager.Instance.LoadedAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetCustomAttributes<NodeContextMenuAttribute>(false).Any())
            .OrderBy(t => t.Name);
}
