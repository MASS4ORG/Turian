using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Turian.Engine.Physics;

/// <summary>
/// Registers the session's <see cref="PhysicsScene"/>, which the ticker steps as a fixed-tick system and core's
/// <see cref="PhysicsQueries"/> uses as its backend.
/// </summary>
public sealed class PhysicsModule : IEngineServiceModule
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<PhysicsScene>();
        services.AddSingleton<IFixedTickSystem>(static sp => sp.GetRequiredService<PhysicsScene>());
        services.AddSingleton<IPhysicsQueryBackend>(static sp => sp.GetRequiredService<PhysicsScene>());
    }
}
