namespace Turian.Engine.Core;

/// <summary>Registers game and package services for one runtime session.</summary>
public interface IEngineServiceModule
{
    /// <summary>Registers services before the session's provider is built.</summary>
    void ConfigureServices(IServiceCollection services);
}
