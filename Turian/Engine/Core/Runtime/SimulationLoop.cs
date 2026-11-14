namespace Turian.Engine.Core;

/// <summary>
/// Drives the game's <see cref="SimulationSession"/> from the game loop: its authoritative tick in
/// <see cref="GameLoopPhase.FixedCommit"/>, after every fixed-tick system has enqueued intent, and its completed-tick
/// event in <see cref="GameLoopPhase.TickCompleted"/>. An application-lifetime subsystem, so it commits before the
/// session's subsystems in the same phase; without a session it runs in no phase.
/// </summary>
/// <param name="session">The authoritative world, or <c>null</c> when the game has none.</param>
/// <param name="clock">The game loop's clock, which the session must share.</param>
public sealed class SimulationLoop(SimulationSession? session, SimulationClock? clock) : IGameLoopSubsystem
{
    /// <inheritdoc />
    public GameLoopPhases Phases => session is null
        ? GameLoopPhases.None
        : GameLoopPhases.FixedCommit | GameLoopPhases.TickCompleted;

    /// <summary>Checks that the session and the game loop share one clock.</summary>
    /// <exception cref="InvalidOperationException">The session runs on a different clock.</exception>
    public void Start()
    {
        if (session is not null && clock is not null && !ReferenceEquals(session.Clock, clock))
        {
            throw new InvalidOperationException("The scene and simulation must share one clock.");
        }
    }

    /// <inheritdoc />
    public void Stop() { }

    /// <inheritdoc />
    public void Run(GameLoopPhase phase, in LoopTime time)
    {
        if (phase == GameLoopPhase.FixedCommit)
        {
            session!.RunTick(time.Tick, time.DeltaTime);
        }
        else
        {
            session!.PublishCompletedTick();
        }
    }
}

/// <summary>Registers the authoritative simulation's driver with the services a game composes.</summary>
public static class SimulationServices
{
    /// <summary>
    /// Registers the <see cref="SimulationLoop"/> that drives the <see cref="SimulationSession"/> service, if the game
    /// registers one, on the game loop's <see cref="SimulationClock"/>.
    /// </summary>
    /// <param name="services">The services being composed.</param>
    public static IServiceCollection AddSimulationLoop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSubsystem(LifetimeScope.Application,
            static scope => new SimulationLoop(scope.Services?.GetService<SimulationSession>(),
                scope.Services?.GetService<SimulationClock>()));
    }
}
