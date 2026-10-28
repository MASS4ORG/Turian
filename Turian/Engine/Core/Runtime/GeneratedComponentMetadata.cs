namespace Turian.Engine.Core;

/// <summary>Component lifecycle and injection metadata supplied by build-time composition.</summary>
/// <param name="Update">Whether the component overrides the update callback.</param>
/// <param name="LateUpdate">Whether the component overrides the late update callback.</param>
/// <param name="FixedUpdate">Whether the component overrides the fixed update callback.</param>
/// <param name="AllowsMultiple">Whether multiple instances may be attached to one node.</param>
/// <param name="RequiredComponents">Component types that must be attached first.</param>
/// <param name="Inject">Compiled service injection, including optional and missing-service behavior.</param>
public sealed record GeneratedComponentMetadata(bool Update, bool LateUpdate, bool FixedUpdate, bool AllowsMultiple,
    Type[] RequiredComponents, Action<Component, IServiceProvider, bool> Inject)
{
    static readonly ConcurrentDictionary<Type, GeneratedComponentMetadata> Components = new();

    /// <summary>Registers metadata for a closed component type.</summary>
    public static void Register<T>(GeneratedComponentMetadata metadata) where T : Component
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Components[typeof(T)] = metadata;
    }

    /// <summary>Runs the compiled injection of a known base component type.</summary>
    public static void InjectBase<T>(Component component, IServiceProvider services, bool allowMissingServices)
        where T : Component
    {
        if (Components.TryGetValue(typeof(T), out var metadata))
        {
            metadata.Inject(component, services, allowMissingServices);
        }
    }

    /// <summary>Resolves an injected service with the required/optional component contract.</summary>
    public static T? ResolveService<T>(IServiceProvider services, bool optional, bool allowMissingServices)
    {
        var service = services.GetService(typeof(T));
        if (service is not null)
        {
            return (T)service;
        }

        return optional || allowMissingServices ? default
            : throw new InvalidOperationException($"Required service '{typeof(T).FullName}' is not registered.");
    }

    internal static bool TryGet(Type type,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GeneratedComponentMetadata? metadata) =>
        Components.TryGetValue(type, out metadata);

    internal static void ReleaseCollectible() => CollectibleAssemblies.RemoveReleased(Components, static type => type);
}
