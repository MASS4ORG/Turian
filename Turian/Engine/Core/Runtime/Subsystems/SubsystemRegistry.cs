namespace Turian.Engine.Core;

/// <summary>
/// One subsystem's registration: its type, lifetime, factory and the subsystems it needs. Registration is explicit
/// and closed, so it works where reflection is unavailable.
/// </summary>
public sealed class SubsystemDescriptor
{
    readonly Func<SubsystemScope, ISubsystem> create;
    readonly Type[] dependencies;
    readonly Type[] runsAfter;

    SubsystemDescriptor(Type type, LifetimeScope scope, Func<SubsystemScope, ISubsystem> create, Type[] dependencies,
        Type[] runsAfter)
    {
        Type = type;
        Scope = scope;
        this.create = create;
        this.dependencies = dependencies;
        this.runsAfter = runsAfter;
    }

    /// <summary>The type the subsystem is resolved by.</summary>
    public Type Type { get; }

    /// <summary>The lifetime that owns the subsystem.</summary>
    public LifetimeScope Scope { get; }

    /// <summary>The subsystem types that must be created and started first.</summary>
    public IReadOnlyList<Type> Dependencies => dependencies;

    /// <summary>
    /// Subsystems this one runs after within each game-loop phase, when they are registered in the same lifetime.
    /// </summary>
    /// <remarks>
    /// This is an ordering hint and nothing more. A target that is not registered is ignored. A target is not
    /// created, started or kept alive for this subsystem, cannot be resolved through it, and does not change the
    /// start or stop order; only <see cref="Dependencies"/> does that. A target in a longer-lived lifetime already
    /// runs earlier in every phase; one in a shorter-lived lifetime cannot, and is rejected.
    /// </remarks>
    public IReadOnlyList<Type> RunsAfter => runsAfter;

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
        return new(typeof(T), scope, owner => create(owner), [.. dependencies], []);
    }

    /// <summary>Returns a copy of this descriptor that also runs after <paramref name="types"/> in each phase.</summary>
    /// <param name="types">Subsystems to run after when registered; see <see cref="RunsAfter"/>.</param>
    public SubsystemDescriptor After(params Type[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        return new(Type, Scope, create, dependencies, [.. runsAfter, .. types]);
    }

    internal ISubsystem Create(SubsystemScope owner) => create(owner);
}

/// <summary>
/// The validated set of subsystems a game composes: each type once, dependencies known and no longer-lived subsystem
/// depending on a shorter-lived one, with a fixed start order and a fixed run order per lifetime.
/// </summary>
public sealed class SubsystemRegistry
{
    readonly SubsystemDescriptor[][] ordered;
    readonly SubsystemDescriptor[][] runOrdered;

    /// <summary>Validates and orders <paramref name="descriptors"/>.</summary>
    /// <param name="descriptors">Every subsystem, in registration order.</param>
    /// <exception cref="InvalidOperationException">
    /// A type repeats, a dependency is unknown or too short-lived, a subsystem runs after a shorter-lived one, or
    /// dependencies or run orderings cycle.
    /// </exception>
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

            foreach (var target in descriptor.RunsAfter)
            {
                if (all.TryGetValue(target, out var earlier) && earlier.Scope > descriptor.Scope)
                {
                    throw new InvalidOperationException(
                        $"{descriptor.Scope} subsystem {descriptor.Type.FullName} cannot run after the shorter-lived "
                        + $"{earlier.Scope} subsystem {target.FullName}: longer lifetimes run first in every phase.");
                }
            }
        }

        var scopes = Enum.GetValues<LifetimeScope>();
        ordered = [.. scopes.Select(scope => Order(registration, all, scope, withRunsAfter: false))];
        runOrdered = [.. scopes.Select(scope => Order(registration, all, scope, withRunsAfter: true))];
    }

    /// <summary>The subsystems a lifetime creates, dependencies before the subsystems that need them.</summary>
    /// <param name="scope">The lifetime.</param>
    public IReadOnlyList<SubsystemDescriptor> For(LifetimeScope scope) => ordered[(int)scope];

    /// <summary>
    /// The order a lifetime's subsystems run in within each game-loop phase: dependencies and
    /// <see cref="SubsystemDescriptor.RunsAfter"/> targets first, otherwise registration order.
    /// </summary>
    /// <param name="scope">The lifetime.</param>
    public IReadOnlyList<SubsystemDescriptor> RunOrder(LifetimeScope scope) => runOrdered[(int)scope];

    static SubsystemDescriptor[] Order(List<SubsystemDescriptor> registration,
        Dictionary<Type, SubsystemDescriptor> all, LifetimeScope scope, bool withRunsAfter)
    {
        var result = new List<SubsystemDescriptor>();
        var state = new Dictionary<Type, bool>();
        foreach (var descriptor in registration.Where(d => d.Scope == scope))
        {
            Visit(descriptor, all, state, result, withRunsAfter);
        }

        return [.. result];
    }

    // state: false while a descriptor's predecessors are being visited, true once it is placed.
    static void Visit(SubsystemDescriptor descriptor, Dictionary<Type, SubsystemDescriptor> all,
        Dictionary<Type, bool> state, List<SubsystemDescriptor> result, bool withRunsAfter)
    {
        if (state.TryGetValue(descriptor.Type, out var done))
        {
            if (done)
            {
                return;
            }

            throw new InvalidOperationException(withRunsAfter
                ? $"Subsystem dependencies and run orderings cycle at {descriptor.Type.FullName}."
                : $"Subsystem dependencies cycle at {descriptor.Type.FullName}.");
        }

        state[descriptor.Type] = false;
        foreach (var dependency in descriptor.Dependencies)
        {
            VisitInScope(dependency);
        }

        if (withRunsAfter)
        {
            foreach (var target in descriptor.RunsAfter)
            {
                VisitInScope(target);
            }
        }

        state[descriptor.Type] = true;
        result.Add(descriptor);

        void VisitInScope(Type type)
        {
            if (all.TryGetValue(type, out var earlier) && earlier.Scope == descriptor.Scope)
            {
                Visit(earlier, all, state, result, withRunsAfter);
            }
        }
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

    /// <summary>
    /// Registers a prepared subsystem descriptor, such as one built with <see cref="SubsystemDescriptor.After"/>.
    /// </summary>
    /// <param name="services">The services being composed.</param>
    /// <param name="descriptor">The subsystem's registration.</param>
    public static IServiceCollection AddSubsystem(this IServiceCollection services, SubsystemDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        return services.AddSingleton(descriptor);
    }
}
