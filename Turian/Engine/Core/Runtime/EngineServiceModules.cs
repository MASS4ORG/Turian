namespace Turian.Engine.Core;

/// <summary>Discovers and runs service modules in a game's resolved assembly graph.</summary>
public static class EngineServiceModules
{
    /// <summary>
    /// Registers modules in graph order, then by type name within each assembly. Repeated assemblies
    /// are scanned only once; later registrations of the same service type take precedence.
    /// </summary>
    public static IServiceCollection AddEngineModules(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var assembly in assemblies.Distinct())
        {
            ArgumentNullException.ThrowIfNull(assembly);
            foreach (var type in GetLoadableTypes(assembly)
                         .Where(static type => type is { IsClass: true, IsAbstract: false }
                                        && typeof(IEngineServiceModule).IsAssignableFrom(type))
                         .OrderBy(static type => type.FullName, StringComparer.Ordinal))
            {
                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new InvalidOperationException(
                        $"Engine service module '{type.FullName}' needs a public parameterless constructor.");
                }

                var module = (IEngineServiceModule)Activator.CreateInstance(type)!;
                module.ConfigureServices(services);
            }
        }

        return services;
    }

    /// <summary>
    /// The assemblies whose modules a game registers: every engine-referencing assembly <paramref name="root"/>
    /// reaches, plus loaded brick assemblies, each after its own references, with <paramref name="root"/> last so the
    /// game's registrations win. Core itself is left out.
    /// </summary>
    /// <param name="root">The game's assembly.</param>
    /// <returns>The assemblies in registration order.</returns>
    public static IReadOnlyList<Assembly> Graph(Assembly root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var core = typeof(IEngineServiceModule).Assembly.GetName().Name!;
        var ordered = new List<Assembly>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { core };

        // The compiler drops references to bricks the game never names, so loaded bricks are visited explicitly.
        foreach (var assembly in PrecastAssemblies.Loaded.Append(root))
        {
            Visit(assembly, core, visited, ordered);
        }

        ordered.Remove(root);
        ordered.Add(root);
        return ordered;
    }

    static void Visit(Assembly assembly, string core, HashSet<string> visited, List<Assembly> ordered)
    {
        if (!visited.Add(assembly.GetName().Name!))
        {
            return;
        }

        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            if (!visited.Contains(reference.Name!) && TryLoad(reference) is { } loaded && ReferencesCore(loaded, core))
            {
                Visit(loaded, core, visited, ordered);
            }
        }

        ordered.Add(assembly);
    }

    static bool ReferencesCore(Assembly assembly, string core) =>
        assembly.GetReferencedAssemblies().Any(reference => reference.Name == core);

    // Prefers an already loaded copy, so a collectible game assembly reaches the bricks the editor loaded.
    static Assembly? TryLoad(AssemblyName reference)
    {
        if (AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == reference.Name) is { } loaded)
        {
            return loaded;
        }

        try
        {
            return Assembly.Load(reference);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
