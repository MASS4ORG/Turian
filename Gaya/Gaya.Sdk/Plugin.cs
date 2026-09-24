namespace Gaya.Sdk;

/// <summary>
/// Marks a class as a Gaya plugin. The host discovers <see cref="IPlugin"/> implementations
/// carrying this attribute, orders them by their <see cref="PluginDependsOnAttribute"/> edges
/// and calls <see cref="IPlugin.Configure"/> once during startup.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PluginAttribute(string id, string displayName) : Attribute
{
    /// <summary>Stable unique id, e.g. <c>gaya.example</c>. Referenced by dependencies.</summary>
    public string Id { get; } = id;

    /// <summary>Human-readable name shown in about / plugin lists.</summary>
    public string DisplayName { get; } = displayName;
}

/// <summary>
/// Declares that the annotated plugin must be configured after the plugin with the given id.
/// Repeatable. A missing target or a cycle is a fatal startup error.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PluginDependsOnAttribute(string pluginId) : Attribute
{
    /// <summary>Id of the plugin that must be configured first.</summary>
    public string PluginId { get; } = pluginId;
}

/// <summary>
/// A Gaya plugin. Implementations do all their wiring in <see cref="Configure"/> — registering
/// services, panels, commands and menu items. Plugins never reference one another's assemblies;
/// they collaborate through shared services resolved from DI and through the host registries.
/// </summary>
public interface IPlugin
{
    /// <summary>Registers this plugin's contributions. Called once, in dependency order.</summary>
    /// <param name="context">The host-provided registration surface.</param>
    void Configure(IPluginContext context);

    /// <summary>
    /// Runs once the host has built the service provider, in the same order as
    /// <see cref="Configure"/>. Anything that needs resolved services — opening a project, publishing
    /// an ambient locator — belongs here rather than in <see cref="Configure"/>.
    /// </summary>
    /// <param name="services">The built provider.</param>
    void Start(IServiceProvider services)
    {
    }

    /// <summary>
    /// Runs once per rendered frame, after the workbench has drawn, in activation order. A plugin
    /// that owns something which must advance whether its panels are visible — a play
    /// session, a file watcher's drain — pumps it here rather than from a panel.
    /// </summary>
    /// <param name="services">The built provider.</param>
    /// <param name="deltaTime">Seconds since the previous frame.</param>
    void Tick(IServiceProvider services, float deltaTime)
    {
    }

    /// <summary>
    /// Runs as the host shuts down, in reverse activation order, while services are still resolvable.
    /// Persisting session state belongs here.
    /// </summary>
    /// <param name="services">The provider, still alive.</param>
    void Stop(IServiceProvider services)
    {
    }
}

/// <summary>The registration surface handed to a plugin during <see cref="IPlugin.Configure"/>.</summary>
public interface IPluginContext
{
    /// <summary>
    /// Service collection shared by every plugin. Register interfaces here; the host builds the
    /// provider after all plugins have been configured. Use <c>TryAdd*</c> to allow overrides.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>Dockable panel contributions.</summary>
    IPanelRegistry Panels { get; }

    /// <summary>Invokable command contributions (menus, key bindings and the palette call these).</summary>
    ICommandRegistry Commands { get; }

    /// <summary>Menu-bar item contributions.</summary>
    IMenuRegistry Menus { get; }

    /// <summary>Key-sequence contributions dispatched to command ids.</summary>
    IShortcutRegistry Shortcuts { get; }

    /// <summary>Contributions to the strips outside the dock space.</summary>
    IChromeRegistry Chrome { get; }

    /// <summary>Chrome contributions drawn inside a panel's own tab strip, in the space the tabs leave over.</summary>
    ITabStripChromeRegistry TabStripChrome { get; }

    /// <summary>Editor-wide settings pages this plugin contributes.</summary>
    ISettingsRegistry Settings { get; }

    /// <summary>Logger scoped with the plugin id.</summary>
    ILogger Logger { get; }

    /// <summary>The process arguments, so a plugin can honor its own switches.</summary>
    IReadOnlyList<string> CommandLineArgs { get; }
}
