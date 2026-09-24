namespace Turian.Engine.Core;

/// <summary>
/// Registers every type marked with <see cref="InternalServiceAttribute"/> into a service collection.
/// </summary>
public static class InternalServiceRegistrar
{
    /// <summary>
    /// Adds the annotated types from the given assemblies. Assemblies are passed explicitly rather
    /// than discovered from the app domain, which would miss anything not yet loaded.
    /// </summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddInternalServices(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var assembly in assemblies)
            foreach (var type in GetLoadableTypes(assembly))
            {
                var attribute = SafeGetAttribute(type);
                if (attribute is null) continue;

                var serviceType = attribute.ServiceType ?? type;

                if (attribute.Lifetime == InternalServiceLifetime.Singleton)
                    services.AddSingleton(serviceType, type);
                else
                    services.AddTransient(serviceType, type);
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
            return ex.Types.Where(type => type is not null).Cast<Type>();
        }
    }

    static InternalServiceAttribute? SafeGetAttribute(Type type)
    {
        try
        {
            return type.GetCustomAttribute<InternalServiceAttribute>();
        }
        catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException)
        {
            return null;
        }
    }
}
