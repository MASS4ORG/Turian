namespace Turian.Editor.Core;

/// <summary>Creates the same reference or script payload for tree rows and grid tiles.</summary>
public static class AssetDragPayloads
{
    /// <summary>Creates a payload carrying the selection when the source belongs to it.</summary>
    public static object Create(AssetEntry entry, IReadOnlyList<AssetEntry> selection, IEnumerable<Assembly> assemblies)
    {
        var carried = selection.Any(candidate => candidate.AbsolutePath == entry.AbsolutePath) ? selection : [entry];
        var name = Path.GetFileNameWithoutExtension(entry.AbsolutePath);
        if (carried.Count == 1 && entry.AbsolutePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && ComponentType(name, assemblies) is { } type)
            return new ScriptDragPayload(type, name, entry.AbsolutePath);
        return new ReferenceDragPayload(entry.AssetMetadata?.Id ?? Guid.Empty, name, entry.AbsolutePath, entry.IsDirectory)
        { Entries = carried };
    }

    static Type? ComponentType(string name, IEnumerable<Assembly> assemblies) => assemblies.SelectMany(Types)
        .FirstOrDefault(type => typeof(Component).IsAssignableFrom(type) && type.Name == name);

    static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
        catch (NotSupportedException) { return []; }
    }
}
