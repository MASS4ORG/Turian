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
    IMenuRegistry Menus)
{
    /// <summary>
    /// Document kind contributions. Register a kind before the panels bound to it, so a layout seeded for it docks
    /// them where the kind asks, and remove what was registered when the lifetime is disposed.
    /// </summary>
    public required IDocumentKindRegistry DocumentKinds { get; init; }
}
