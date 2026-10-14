namespace Turian.Engine.Core;

/// <summary>
/// A session service the <see cref="SceneTicker"/> drives on its fixed-tick boundary, such as a physics world.
/// Register implementations in the session's service container; hosts pass them to the ticker.
/// </summary>
public interface IFixedTickSystem
{
    /// <summary>Runs after every component's <see cref="Component.OnFixedUpdate"/> of the tick.</summary>
    /// <param name="tick">The tick being executed.</param>
    /// <param name="deltaTime">The fixed interval, in seconds.</param>
    void FixedTick(long tick, double deltaTime);

    /// <summary>Runs once per frame after its fixed ticks and before <see cref="Component.OnUpdate"/>.</summary>
    /// <param name="alpha">How far presentation time lies between the last tick and the next one, in [0, 1].</param>
    void AfterFixedTicks(double alpha);
}
