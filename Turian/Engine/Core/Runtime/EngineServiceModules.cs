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
                         .Where(type => type.IsClass && !type.IsAbstract
                                        && typeof(IEngineServiceModule).IsAssignableFrom(type))
                         .OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (type.GetConstructor(Type.EmptyTypes) is null)
                    throw new InvalidOperationException(
                        $"Engine service module '{type.FullName}' needs a public parameterless constructor.");

                var module = (IEngineServiceModule)Activator.CreateInstance(type)!;
                module.ConfigureServices(services);
            }
        }

        return services;
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
