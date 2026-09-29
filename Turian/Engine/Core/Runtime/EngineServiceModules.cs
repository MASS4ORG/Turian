namespace Turian.Engine.Core;

/// <summary>Discovers and runs service modules in a game's compiled assembly.</summary>
public static class EngineServiceModules
{
    /// <summary>Registers the modules in an explicitly selected user-code assembly.</summary>
    public static IServiceCollection AddEngineModules(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in GetLoadableTypes(assembly)
                     .Where(type => type.IsClass && !type.IsAbstract
                                    && typeof(IEngineServiceModule).IsAssignableFrom(type))
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException($"Engine service module '{type.FullName}' needs a public parameterless constructor.");

            var module = (IEngineServiceModule)Activator.CreateInstance(type)!;
            module.ConfigureServices(services);
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
