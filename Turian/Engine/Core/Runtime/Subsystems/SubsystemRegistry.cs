namespace Turian.Engine.Core;

/// <summary>
/// One subsystem's registration: its type, lifetime, factory and the subsystems it needs. Registration is explicit
/// and closed, so it works where reflection is unavailable.
/// </summary>
public sealed class SubsystemDescriptor
{
    readonly Func<SubsystemScope, ISubsystem> create;
    readonly Type[] dependencies;

    SubsystemDescriptor(Type type, LifetimeScope scope, Func<SubsystemScope, ISubsystem> create, Type[] dependencies)
    {
        Type = type;
        Scope = scope;
        this.create = create;
        this.dependencies = dependencies;
    }

    /// <summary>The type the subsystem is resolved by.</summary>
    public Type Type { get; }

    /// <summary>The lifetime that owns the subsystem.</summary>
    public LifetimeScope Scope { get; }

    /// <summary>The subsystem types that must be created and started first.</summary>
    public IReadOnlyList<Type> Dependencies => dependencies;

    /// <summary>Describes a subsystem.</summary>
    /// <typeparam name="T">The subsystem type, also the type it is resolved by.</typeparam>
    /// <param name="scope">The lifetime that owns it.</param>
    /// <param name="create">Builds it; dependencies are already started and resolvable from the given scope.</param>
    /// <param name="dependencies">The subsystems it needs, each in the same or a longer-lived scope.</param>
    public static SubsystemDescriptor Of<T>(LifetimeScope scope, Func<SubsystemScope, T> create,
        params Type[] dependencies) where T : class, ISubsystem
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(dependencies);
        return new(typeof(T), scope, owner => create(owner), [.. dependencies]);
    }

    internal ISubsystem Create(SubsystemScope owner) => create(owner);
}

/// <summary>
/// The validated set of subsystems a game composes: each type once, dependencies known and no longer-lived subsystem
/// depending on a shorter-lived one, with a fixed start order per lifetime.
/// </summary>
public sealed class SubsystemRegistry
{
    readonly SubsystemDescriptor[][] ordered;

    /// <summary>Validates and orders <paramref name="descriptors"/>.</summary>
    /// <param name="descriptors">Every subsystem, in registration order.</param>
    /// <exception cref="InvalidOperationException">A type repeats, a dependency is unknown or too short-lived, or dependencies cycle.</exception>
    public SubsystemRegistry(IEnumerable<SubsystemDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var all = new Dictionary<Type, SubsystemDescriptor>();
        var registration = new List<SubsystemDescriptor>();
        foreach (var descriptor in descriptors)
        {
            if (!all.TryAdd(descriptor.Type, descriptor))
            {
                throw new InvalidOperationException($"Subsystem {descriptor.Type.FullName} is registered twice.");
            }

            registration.Add(descriptor);
        }

        foreach (var descriptor in registration)
        {
            foreach (var dependency in descriptor.Dependencies)
            {
                if (!all.TryGetValue(dependency, out var needed))
                {
                    throw new InvalidOperationException(
                        $"Subsystem {descriptor.Type.FullName} depends on unregistered {dependency.FullName}.");
                }

                if (needed.Scope > descriptor.Scope)
                {
                    throw new InvalidOperationException(
                        $"{descriptor.Scope} subsystem {descriptor.Type.FullName} cannot depend on the shorter-lived "
                        + $"{needed.Scope} subsystem {dependency.FullName}.");
                }
            }
        }

        ordered = [.. Enum.GetValues<LifetimeScope>().Select(scope => Order(registration, all, scope))];
    }

    /// <summary>The subsystems a lifetime creates, dependencies before the subsystems that need them.</summary>
    /// <param name="scope">The lifetime.</param>
    public IReadOnlyList<SubsystemDescriptor> For(LifetimeScope scope) => ordered[(int)scope];

    static SubsystemDescriptor[] Order(List<SubsystemDescriptor> registration,
        Dictionary<Type, SubsystemDescriptor> all, LifetimeScope scope)
    {
        var result = new List<SubsystemDescriptor>();
        var state = new Dictionary<Type, bool>();
        foreach (var descriptor in registration.Where(d => d.Scope == scope))
        {
            Visit(descriptor, all, state, result);
        }

        return [.. result];
    }

    // state: false while a descriptor's dependencies are being visited, true once it is placed.
    static void Visit(SubsystemDescriptor descriptor, Dictionary<Type, SubsystemDescriptor> all,
        Dictionary<Type, bool> state, List<SubsystemDescriptor> result)
    {
        if (state.TryGetValue(descriptor.Type, out var done))
        {
            if (done)
            {
                return;
            }

            throw new InvalidOperationException($"Subsystem dependencies cycle at {descriptor.Type.FullName}.");
        }

        state[descriptor.Type] = false;
        foreach (var dependency in descriptor.Dependencies)
        {
            var needed = all[dependency];
            if (needed.Scope == descriptor.Scope)
            {
                Visit(needed, all, state, result);
            }
        }

        state[descriptor.Type] = true;
        result.Add(descriptor);
    }
}

/// <summary>Registers subsystems with the services a game's modules compose.</summary>
public static class SubsystemServices
{
    /// <summary>Registers a subsystem; the runtime builds its <see cref="SubsystemRegistry"/> from every registration.</summary>
    /// <typeparam name="T">The subsystem type, also the type it is resolved by.</typeparam>
    /// <param name="services">The services being composed.</param>
    /// <param name="scope">The lifetime that owns it.</param>
    /// <param name="create">Builds it; dependencies are already started and resolvable from the given scope.</param>
    /// <param name="dependencies">The subsystems it needs, each in the same or a longer-lived scope.</param>
    public static IServiceCollection AddSubsystem<T>(this IServiceCollection services, LifetimeScope scope,
        Func<SubsystemScope, T> create, params Type[] dependencies) where T : class, ISubsystem
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton(SubsystemDescriptor.Of(scope, create, dependencies));
    }
}
