namespace Gaya.Sdk;

/// <summary>Contributes editor features through the running host's services and registries.</summary>
public interface ILivePlugin
{
    /// <summary>Activates this plugin and returns the lifetime that releases all its contributions.</summary>
    IDisposable Activate(LivePluginContext context);
}

/// <summary>The running editor services and registries available to a live plugin.</summary>
/// <param name="Services">The existing host services.</param>
/// <param name="Panels">Dockable panel contributions.</param>
/// <param name="Commands">Command contributions.</param>
/// <param name="Menus">Menu contributions.</param>
public sealed record LivePluginContext(IServiceProvider Services, IPanelRegistry Panels, ICommandRegistry Commands,
    IMenuRegistry Menus);
