namespace Turian.Engine.Core;

/// <summary>How long a subsystem lives: the whole run, one gameplay session, or one loaded scene.</summary>
public enum LifetimeScope
{
    /// <summary>Created when the application starts and ended with it.</summary>
    Application,

    /// <summary>Created when a gameplay session begins, survives scene loads and ends with the session.</summary>
    Session,

    /// <summary>Created when a scene loads and ended when it unloads.</summary>
    Scene,
}

/// <summary>The part of a frame in which a ticking subsystem runs.</summary>
public enum SubsystemTickPhase
{
    /// <summary>Once per simulation step.</summary>
    Fixed,

    /// <summary>Once per rendered frame.</summary>
    Frame,
}

/// <summary>
/// A service owned by a <see cref="LifetimeScope"/>: created after the subsystems it depends on, started on the main
/// thread, and stopped before them. Implementations that hold resources also implement <see cref="IDisposable"/>.
/// </summary>
public interface ISubsystem
{
    /// <summary>Starts the subsystem once every subsystem it depends on has started.</summary>
    void Start();

    /// <summary>Stops the subsystem before the subsystems it depends on stop.</summary>
    void Stop();
}

/// <summary>A subsystem that runs once per simulation step or frame.</summary>
public interface ITickingSubsystem : ISubsystem
{
    /// <summary>The phase in which <see cref="Tick"/> runs.</summary>
    SubsystemTickPhase TickPhase { get; }

    /// <summary>Advances the subsystem.</summary>
    /// <param name="deltaTime">Seconds since its previous tick in the same phase.</param>
    void Tick(float deltaTime);
}
