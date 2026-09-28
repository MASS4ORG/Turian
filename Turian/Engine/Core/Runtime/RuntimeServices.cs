namespace Turian.Engine.Core;

/// <summary>
/// Provides access to runtime-wide engine services for systems that are not created via dependency injection.
/// </summary>
public static class RuntimeServices
{
    static IServiceProvider? _services;

    /// <summary>
    /// Gets a value indicating whether runtime services have been configured.
    /// </summary>
    public static bool IsConfigured => _services is not null;

    /// <summary>
    /// Configures the runtime service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider to expose globally for runtime lookups.</param>
    public static void Configure(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _services = serviceProvider;
    }

    /// <summary>
    /// Clears the configured runtime service provider.
    /// </summary>
    public static void Reset()
    {
        _services = null;
    }

    /// <summary>
    /// Resolves a required service from the configured runtime service provider.
    /// </summary>
    /// <typeparam name="T">The service type to resolve.</typeparam>
    /// <returns>The resolved service instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when runtime services have not been configured or the requested service cannot be resolved.
    /// </exception>
    public static T GetRequired<T>() where T : class =>
        TryGet<T>() ?? throw new InvalidOperationException($"Runtime service '{typeof(T).FullName}' is not available.");

    /// <summary>
    /// Tries to resolve a service from the configured runtime service provider.
    /// </summary>
    /// <typeparam name="T">The service type to resolve.</typeparam>
    /// <returns>The resolved service instance, or <c>null</c> if unavailable.</returns>
    public static T? TryGet<T>() where T : class => _services?.GetService(typeof(T)) as T;
}
