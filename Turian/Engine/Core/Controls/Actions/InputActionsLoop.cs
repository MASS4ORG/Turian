using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Turian.Engine.Core;

/// <summary>
/// Reads the game's <see cref="InputActionService"/> at <see cref="GameLoopPhase.BeginFrame"/>, so every fixed tick
/// and update of the frame sees the same action state. An application-lifetime subsystem; without an
/// <see cref="InputActionService"/> it runs in no phase.
/// </summary>
/// <param name="actions">The actions to advance, or <c>null</c> when the game has none.</param>
public sealed class InputActionsLoop(InputActionService? actions) : IGameLoopSubsystem
{
    /// <inheritdoc />
    public GameLoopPhases Phases => actions is null ? GameLoopPhases.None : GameLoopPhases.BeginFrame;

    /// <inheritdoc />
    public void Start() { }

    /// <inheritdoc />
    public void Stop() { }

    /// <inheritdoc />
    public void Run(GameLoopPhase phase, in LoopTime time) => actions?.Update();
}

/// <summary>Registers input actions with the services a game composes.</summary>
public static class InputActionServices
{
    /// <summary>
    /// Registers an <see cref="InputActionService"/> over the game's <see cref="IInputSource"/>, unless one is already
    /// registered, as <see cref="IInputActions"/> too, and the <see cref="InputActionsLoop"/> that reads it each frame.
    /// </summary>
    /// <param name="services">The services being composed.</param>
    public static IServiceCollection AddInputActions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(static sp => new InputActionService(sp.GetRequiredService<IInputSource>()));
        services.TryAddSingleton<IInputActions>(static sp => sp.GetRequiredService<InputActionService>());
        return services.AddSubsystem(LifetimeScope.Application,
            static scope => new InputActionsLoop(scope.Services?.GetService<InputActionService>()));
    }
}
