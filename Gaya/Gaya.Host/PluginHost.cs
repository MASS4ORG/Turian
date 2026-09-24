using Microsoft.Extensions.Logging.Abstractions;

namespace Gaya.Host;

/// <summary>
/// The activated set of plugins: the built service provider plus the populated contribution
/// registries the <see cref="Workbench"/> renders from.
/// </summary>
public sealed class GayaApplication(
    ServiceProvider services,
    PanelRegistry panels,
    CommandRegistry commands,
    MenuRegistry menus,
    ChromeRegistry chrome,
    TabStripChromeRegistry tabStripChrome,
    ShortcutService shortcuts,
    FocusTracker focus,
    IReadOnlyList<string> loadedPluginIds,
    IReadOnlyList<IPlugin>? plugins = null,
    EditorSettings? settings = null,
    ThemeService? themes = null,
    ILogger? logger = null) : IDisposable
{
    ILogger Logger => logger ?? NullLogger.Instance;

    /// <summary>The configured plugins, in activation order, for the per-frame tick.</summary>
    public IReadOnlyList<IPlugin> Plugins { get; } = plugins ?? [];

    /// <summary>Root service provider; plugins and panels resolve their dependencies from here.</summary>
    public ServiceProvider Services { get; } = services;

    /// <summary>Panel contributions.</summary>
    public PanelRegistry Panels { get; } = panels;

    /// <summary>Command contributions.</summary>
    public CommandRegistry Commands { get; } = commands;

    /// <summary>Menu-bar contributions.</summary>
    public MenuRegistry Menus { get; } = menus;

    /// <summary>Chrome contributions drawn outside the dock space.</summary>
    public ChromeRegistry Chrome { get; } = chrome;

    /// <summary>Chrome contributions drawn inside a panel's tab strip.</summary>
    public TabStripChromeRegistry TabStripChrome { get; } = tabStripChrome;

    /// <summary>Key sequences the workbench dispatches, with the user's overrides applied.</summary>
    public ShortcutService Shortcuts { get; } = shortcuts;

    /// <summary>Which panel is focused, and therefore which panel-scoped shortcuts fire.</summary>
    public FocusTracker Focus { get; } = focus;

    /// <summary>Editor-wide settings pages and their persistence.</summary>
    public EditorSettings Settings { get; } = settings ?? new EditorSettings(logger ?? NullLogger.Instance);

    /// <summary>The theme everything is drawn with.</summary>
    public ThemeService Themes { get; } = themes ?? new ThemeService();

    /// <summary>Ids of every plugin that was configured, in activation order.</summary>
    public IReadOnlyList<string> LoadedPluginIds { get; } = loadedPluginIds;

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var plugin in Plugins.Reverse())
        {
            try
            {
                plugin.Stop(Services);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Plugin {Plugin} failed to stop", plugin.GetType().Name);
            }
        }

        Services.Dispose();
    }
}

/// <summary>
/// Discovers <see cref="IPlugin"/> types across a set of assemblies, orders them by their
/// <see cref="PluginDependsOnAttribute"/> edges, calls <see cref="IPlugin.Configure"/> on each and
/// builds the shared service provider.
/// </summary>
public static class PluginHost
{
    /// <summary>Loads and configures every plugin found in <paramref name="assemblies"/>.</summary>
    /// <param name="assemblies">Assemblies to scan for <c>[Plugin]</c> <see cref="IPlugin"/> types.</param>
    /// <param name="logger">Host logger; shared by every plugin.</param>
    /// <param name="configureServices">Optional hook to seed host services before plugins run.</param>
    /// <param name="commandLineArgs">Process arguments handed to each plugin through its context.</param>
    public static GayaApplication Load(
        IEnumerable<Assembly> assemblies,
        ILogger logger,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyList<string>? commandLineArgs = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(logger);

        var discovered = Discover(assemblies).ToList();
        var ordered = TopologicalSort(discovered);

        var services = new ServiceCollection();
        services.AddSingleton(logger);
        configureServices?.Invoke(services);

        // Settings and themes are services as well as registries: a plugin contributes pages through
        // the context, and anything that reads a setting resolves the service.
        var settings = new EditorSettings(logger);
        var themes = new ThemeService();
        services.AddSingleton<IEditorSettings>(settings);
        services.AddSingleton<IThemeService>(themes);

        var panels = new PanelRegistry();
        var commands = new CommandRegistry();
        var menus = new MenuRegistry();
        var chrome = new ChromeRegistry();
        var tabStripChrome = new TabStripChromeRegistry();
        var shortcuts = new ShortcutService(logger, settings);
        var focus = new FocusTracker();

        services.AddSingleton<ICommandCatalog>(commands);
        services.AddSingleton<IShortcutService>(shortcuts);
        services.AddSingleton<IFocusTracker>(focus);

        RegisterShellCommands(commands, shortcuts);

        var args = commandLineArgs ?? [];
        var loaded = new List<string>();
        var instances = new List<(PluginAttribute Attr, IPlugin Plugin)>();

        foreach (var (attr, type) in ordered)
        {
            var plugin = (IPlugin)Activator.CreateInstance(type)!;
            var context = new PluginContext(services, panels, commands, menus, chrome, tabStripChrome,
                shortcuts, settings, logger, args);
            logger.LogDebug("Configuring plugin {PluginId} ({DisplayName})", attr.Id, attr.DisplayName);
            plugin.Configure(context);
            loaded.Add(attr.Id);
            instances.Add((attr, plugin));
        }

        var provider = services.BuildServiceProvider(validateScopes: true);

        foreach (var (attr, plugin) in instances)
        {
            try
            {
                plugin.Start(provider);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Plugin {PluginId} failed to start", attr.Id);
            }
        }

        logger.LogInformation("Gaya loaded {Count} plugin(s): {Plugins}", loaded.Count, string.Join(", ", loaded));
        return new GayaApplication(provider, panels, commands, menus, chrome, tabStripChrome, shortcuts,
            focus, loaded, [.. instances.Select(entry => entry.Plugin)], settings, themes, logger);
    }

    /// <summary>
    /// The workbench's own commands, registered before any plugin so a plugin may rebind or replace
    /// them. They act on <see cref="IShellHost"/>, which the application supplies.
    /// </summary>
    static void RegisterShellCommands(CommandRegistry commands, ShortcutService shortcuts)
    {
        commands.Register(new CommandDescriptor(ShellCommands.CommandPalette, "View: Command Palette",
            services => services.GetRequiredService<IShellHost>().ToggleCommandPalette())
        { MenuLabel = "Command Palette" });

        shortcuts.Add(new KeyBinding(ShellCommands.CommandPalette, KeyboardKey.P,
            KeyModifiers.Ctrl | KeyModifiers.Shift));
    }

    static IEnumerable<(PluginAttribute Attr, Type Type)> Discover(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type is { IsClass: true, IsAbstract: false }
                    && typeof(IPlugin).IsAssignableFrom(type)
                    && type.GetCustomAttribute<PluginAttribute>() is { } attr)
                {
                    yield return (attr, type);
                }
            }
        }
    }

    static List<(PluginAttribute Attr, Type Type)> TopologicalSort(
        List<(PluginAttribute Attr, Type Type)> plugins)
    {
        var byId = plugins.ToDictionary(p => p.Attr.Id);
        var result = new List<(PluginAttribute, Type)>();
        var state = new Dictionary<string, int>(); // 0 = unseen, 1 = in progress, 2 = done

        void Visit(string id)
        {
            if (state.GetValueOrDefault(id) == 2) return;
            if (state.GetValueOrDefault(id) == 1)
                throw new InvalidOperationException($"Plugin dependency cycle involving '{id}'.");
            if (!byId.TryGetValue(id, out var plugin))
                throw new InvalidOperationException($"Plugin depends on missing plugin '{id}'.");

            state[id] = 1;
            foreach (var dep in plugin.Type.GetCustomAttributes<PluginDependsOnAttribute>())
                Visit(dep.PluginId);
            state[id] = 2;
            result.Add(plugin);
        }

        foreach (var plugin in plugins.OrderBy(p => p.Attr.Id, StringComparer.Ordinal))
            Visit(plugin.Attr.Id);

        return result;
    }

    sealed class PluginContext(
        IServiceCollection services,
        IPanelRegistry panels,
        ICommandRegistry commands,
        IMenuRegistry menus,
        IChromeRegistry chrome,
        ITabStripChromeRegistry tabStripChrome,
        IShortcutRegistry shortcuts,
        ISettingsRegistry settings,
        ILogger logger,
        IReadOnlyList<string> commandLineArgs) : IPluginContext
    {
        public IServiceCollection Services => services;
        public IPanelRegistry Panels => panels;
        public ICommandRegistry Commands => commands;
        public IMenuRegistry Menus => menus;
        public IChromeRegistry Chrome => chrome;
        public ITabStripChromeRegistry TabStripChrome => tabStripChrome;
        public IShortcutRegistry Shortcuts => shortcuts;
        public ISettingsRegistry Settings => settings;
        public ILogger Logger => logger;
        public IReadOnlyList<string> CommandLineArgs => commandLineArgs;
    }
}
