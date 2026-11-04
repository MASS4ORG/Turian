namespace Turian.Editor.Core;

/// <summary>
/// Collectible load context for user assemblies.
/// </summary>
sealed class UserAssemblyLoadContext : AssemblyLoadContext
{
    AssemblyDependencyResolver? resolver;
    string? probingDirectory;

    public UserAssemblyLoadContext()
        : base("UserAssemblyLoadContext", isCollectible: true)
    {
    }

    public void SetResolver(string assemblyPath)
    {
        resolver = new AssemblyDependencyResolver(assemblyPath);
        probingDirectory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
    }

    protected override ReflectionAssembly? Load(AssemblyName assemblyName)
    {
        if (resolver != null)
        {
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null)
            {
                return LoadFromAssemblyPath(path);
            }
        }

        // Sibling user assemblies are emitted without a deps.json, so the resolver does not know them.
        if (probingDirectory != null && assemblyName.Name is { } name
            && Path.Combine(probingDirectory, $"{name}.dll") is var sibling && File.Exists(sibling))
        {
            return LoadFromFileBytes(sibling);
        }

        try { return Default.LoadFromAssemblyName(assemblyName); }
        catch { /* fallthrough */ }

        return null;
    }

    /// <summary>
    /// Loads an assembly, and its symbols when present, from a copy in memory. A path load maps the file until the
    /// context is collected, and Windows then refuses the next compile into the slot or deleting it.
    /// </summary>
    public ReflectionAssembly LoadFromFileBytes(string path)
    {
        using var assembly = new MemoryStream(File.ReadAllBytes(path));
        var pdb = Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(pdb))
        {
            return LoadFromStream(assembly);
        }

        using var symbols = new MemoryStream(File.ReadAllBytes(pdb));
        return LoadFromStream(assembly, symbols);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        if (resolver != null)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path != null)
            {
                return LoadUnmanagedDllFromPath(path);
            }
        }

        return base.LoadUnmanagedDll(unmanagedDllName);
    }
}
