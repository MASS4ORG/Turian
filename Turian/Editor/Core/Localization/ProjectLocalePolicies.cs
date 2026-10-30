namespace Turian.Editor.Core;

/// <summary>Finds the <see cref="IProjectLocalePolicy"/> implementations the enabled bricks contribute.</summary>
public static class ProjectLocalePolicies
{
    /// <summary>Creates one instance of every policy type in the loaded, non-retired brick assemblies.</summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <param name="log">Where a policy that cannot be created is reported.</param>
    /// <returns>The policies.</returns>
    public static IReadOnlyList<IProjectLocalePolicy> Discover(IEnumerable<ReflectionAssembly> assemblies, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(log);

        var contract = typeof(IProjectLocalePolicy).Assembly.GetName().Name;
        var policies = new List<IProjectLocalePolicy>();
        foreach (var assembly in assemblies.Where(assembly => !assembly.IsDynamic && !BrickAssemblies.IsRetired(assembly)
                     && assembly.GetReferencedAssemblies().Any(reference => reference.Name == contract)))
        {
            foreach (var type in LoadableTypes(assembly).Where(static type => type is { IsClass: true, IsAbstract: false }
                         && typeof(IProjectLocalePolicy).IsAssignableFrom(type)
                         && type.GetConstructor(Type.EmptyTypes) is not null))
            {
                try
                {
                    policies.Add((IProjectLocalePolicy)Activator.CreateInstance(type)!);
                }
                catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException)
                {
                    log.LogWarning(ex, "Skipping locale policy {Type}", type.FullName);
                }
            }
        }

        return policies;
    }

    /// <summary>The first locale a policy chooses for the project.</summary>
    /// <param name="policies">The policies, in precedence order.</param>
    /// <param name="settings">The project's settings.</param>
    /// <returns>The locale, or <c>null</c> when no policy chooses one.</returns>
    public static string? DefaultLocale(IEnumerable<IProjectLocalePolicy> policies, IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(settings);

        return policies.Select(policy => policy.DefaultLocale(settings))
            .FirstOrDefault(static locale => !string.IsNullOrWhiteSpace(locale));
    }

    static IEnumerable<Type> LoadableTypes(ReflectionAssembly assembly)
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
