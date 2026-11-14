namespace Turian.Engine.Core;

/// <summary>
/// The parts of one frame that <see cref="SceneTicker"/> runs, in order. <see cref="FixedTick"/>,
/// <see cref="FixedCommit"/> and <see cref="TickCompleted"/> run once per fixed tick, so between zero and
/// <see cref="SimulationClock.MaxTicksPerFrame"/> times a frame; the others run exactly once per frame.
/// </summary>
/// <remarks>
/// The fixed-step accumulator, the catch-up cap and <see cref="TimeSettings.DropBacklog"/> belong to
/// <see cref="SimulationClock"/>, between <see cref="BeginFrame"/> and <see cref="AfterFixedTicks"/>. No phase can
/// change how many ticks a frame runs.
/// </remarks>
public enum GameLoopPhase
{
    /// <summary>Once per frame, before any fixed tick: scenes have started and input actions are read here.</summary>
    BeginFrame,

    /// <summary>Once per fixed tick, after every component's <see cref="Component.OnFixedUpdate"/>: gameplay systems such as physics.</summary>
    FixedTick,

    /// <summary>
    /// Once per fixed tick, after <see cref="FixedTick"/>: the authoritative simulation commits and worker events
    /// drain. Destruction requested during the tick is applied when this phase ends.
    /// </summary>
    FixedCommit,

    /// <summary>Once after each fixed tick that succeeded, including catch-up ticks; never once per frame.</summary>
    TickCompleted,

    /// <summary>
    /// Once per frame, even when it ran no fixed tick, after the clock has applied its cap and dropped backlog:
    /// presentation interpolates with <see cref="LoopTime.Alpha"/>.
    /// </summary>
    AfterFixedTicks,

    /// <summary>Once per frame, after every component's <see cref="Component.OnUpdate"/>.</summary>
    Update,

    /// <summary>Once per frame, after every component's <see cref="Component.OnLateUpdate"/>.</summary>
    LateUpdate,

    /// <summary>
    /// Once per frame, after destruction requested by the presentation phases is applied and before a requested
    /// session restart.
    /// </summary>
    EndFrame,
}

/// <summary>The set of <see cref="GameLoopPhase"/> values a <see cref="IGameLoopSubsystem"/> runs in.</summary>
[Flags]
public enum GameLoopPhases
{
    /// <summary>No phase.</summary>
    None = 0,

    /// <summary><see cref="GameLoopPhase.BeginFrame"/>.</summary>
    BeginFrame = 1 << GameLoopPhase.BeginFrame,

    /// <summary><see cref="GameLoopPhase.FixedTick"/>.</summary>
    FixedTick = 1 << GameLoopPhase.FixedTick,

    /// <summary><see cref="GameLoopPhase.FixedCommit"/>.</summary>
    FixedCommit = 1 << GameLoopPhase.FixedCommit,

    /// <summary><see cref="GameLoopPhase.TickCompleted"/>.</summary>
    TickCompleted = 1 << GameLoopPhase.TickCompleted,

    /// <summary><see cref="GameLoopPhase.AfterFixedTicks"/>.</summary>
    AfterFixedTicks = 1 << GameLoopPhase.AfterFixedTicks,

    /// <summary><see cref="GameLoopPhase.Update"/>.</summary>
    Update = 1 << GameLoopPhase.Update,

    /// <summary><see cref="GameLoopPhase.LateUpdate"/>.</summary>
    LateUpdate = 1 << GameLoopPhase.LateUpdate,

    /// <summary><see cref="GameLoopPhase.EndFrame"/>.</summary>
    EndFrame = 1 << GameLoopPhase.EndFrame,
}

/// <summary>The time a phase runs at.</summary>
/// <param name="Tick">
/// In the fixed phases, the tick being executed or just completed; elsewhere, the last completed tick.
/// </param>
/// <param name="DeltaTime">
/// In the fixed phases, the fixed interval; elsewhere, the frame's scaled delta, zero while the clock is paused.
/// </param>
/// <param name="UnscaledDeltaTime">The delta before time scale and pause; the fixed interval in the fixed phases.</param>
/// <param name="Alpha">
/// How far presentation time lies between the last tick and the next one, in [0, 1]. In
/// <see cref="GameLoopPhase.BeginFrame"/> it is the previous frame's value; in the fixed phases it is zero.
/// </param>
public readonly record struct LoopTime(long Tick, double DeltaTime, double UnscaledDeltaTime, double Alpha);

/// <summary>
/// A subsystem that runs in one or more phases of the game loop. Within a phase, the application's subsystems run
/// first, then the session's, then each loaded scene's in load order; within a lifetime they run in the
/// <see cref="SubsystemRegistry"/> order: dependencies and <see cref="SubsystemDescriptor.RunsAfter"/> targets first,
/// otherwise registration order.
/// </summary>
/// <remarks>
/// A subsystem implementing this interface is driven only through it, even if it also implements
/// <see cref="ITickingSubsystem"/> or <see cref="IFixedTickSystem"/>.
/// </remarks>
public interface IGameLoopSubsystem : ISubsystem
{
    /// <summary>The phases <see cref="Run"/> is called in; read once, when the subsystem's lifetime starts.</summary>
    GameLoopPhases Phases { get; }

    /// <summary>Runs the subsystem's part of <paramref name="phase"/>.</summary>
    /// <param name="phase">One of the phases in <see cref="Phases"/>.</param>
    /// <param name="time">The time the phase runs at.</param>
    void Run(GameLoopPhase phase, in LoopTime time);
}

/// <summary>Maps the earlier subsystem interfaces onto game-loop phases.</summary>
static class GameLoopPhaseDispatch
{
    /// <summary>The phase count, for per-phase tables.</summary>
    internal const int Count = (int)GameLoopPhase.EndFrame + 1;

    /// <summary>The phases <paramref name="subsystem"/> runs in.</summary>
    /// <remarks>
    /// <see cref="IFixedTickSystem"/> runs in <see cref="GameLoopPhase.FixedTick"/> and
    /// <see cref="GameLoopPhase.AfterFixedTicks"/>; an <see cref="ITickingSubsystem"/> runs in
    /// <see cref="GameLoopPhase.FixedTick"/> or <see cref="GameLoopPhase.Update"/> by its
    /// <see cref="SubsystemTickPhase"/>.
    /// </remarks>
    internal static GameLoopPhases PhasesOf(ISubsystem subsystem)
    {
        if (subsystem is IGameLoopSubsystem loop)
        {
            return loop.Phases;
        }

        var phases = GameLoopPhases.None;
        if (subsystem is IFixedTickSystem)
        {
            phases |= GameLoopPhases.FixedTick | GameLoopPhases.AfterFixedTicks;
        }

        if (subsystem is ITickingSubsystem ticking)
        {
            phases |= ticking.TickPhase == SubsystemTickPhase.Fixed ? GameLoopPhases.FixedTick : GameLoopPhases.Update;
        }

        return phases;
    }

    /// <summary>Runs <paramref name="subsystem"/>'s part of <paramref name="phase"/>, one of the phases it runs in.</summary>
    internal static void Run(ISubsystem subsystem, GameLoopPhase phase, in LoopTime time)
    {
        if (subsystem is IGameLoopSubsystem loop)
        {
            loop.Run(phase, in time);
            return;
        }

        switch (phase)
        {
            case GameLoopPhase.FixedTick:
                (subsystem as IFixedTickSystem)?.FixedTick(time.Tick, time.DeltaTime);
                if (subsystem is ITickingSubsystem { TickPhase: SubsystemTickPhase.Fixed } fixedTicker)
                {
                    fixedTicker.Tick((float)time.DeltaTime);
                }

                break;
            case GameLoopPhase.AfterFixedTicks:
                (subsystem as IFixedTickSystem)?.AfterFixedTicks(time.Alpha);
                break;
            case GameLoopPhase.Update:
                (subsystem as ITickingSubsystem)?.Tick((float)time.DeltaTime);
                break;
        }
    }
}
