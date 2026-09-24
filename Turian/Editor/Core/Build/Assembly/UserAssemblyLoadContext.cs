namespace Turian.Editor.Core;

/// <summary>
/// Collectible load context for user assemblies.
/// </summary>
sealed class UserAssemblyLoadContext : AssemblyLoadContext
{
    AssemblyDependencyResolver? resolver;

    public UserAssemblyLoadContext()
        : base("UserAssemblyLoadContext", isCollectible: true)
    {
    }

    public void SetResolver(string assemblyPath)
    {
        resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (resolver != null)
        {
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null)
                return LoadFromAssemblyPath(path);
        }

        try { return Default.LoadFromAssemblyName(assemblyName); }
        catch { /* fallthrough */ }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        if (resolver != null)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path != null)
                return LoadUnmanagedDllFromPath(path);
        }

        return base.LoadUnmanagedDll(unmanagedDllName);
    }
}
