namespace Gaya.Plugin.Turian;

/// <summary>
/// Built-in plugin that turns the bare Gaya workbench into the Turian game studio: the scene
/// tree, inspector and output panels, plus their menu entries. It registers the framework-agnostic
/// controllers from <c>Turian.Editor.Core</c> and opens the project named by <c>--project</c>.
/// </summary>
[Plugin("gaya.turian", "Turian Game Studio")]
public sealed class GayaPlugin : IPlugin
{
    IReadOnlyList<string> args = [];
    ILogger log = Log.Logger;

    /// <inheritdoc />
    public void Configure(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        args = context.CommandLineArgs;
        log = context.Logger;
        context.Services.AddEditorServices(context.Logger);
        context.Services.AddSingleton<IUiBlocker, BackgroundTaskUiBlocker>();
        context.Services.AddSingleton<StudioLocalization>();
        context.Services.AddSingleton<IShellLocalization>(sp => sp.GetRequiredService<StudioLocalization>());
        context.Services.AddSingleton<LocaleService>();
        context.Services.AddSingleton(sp => new ProjectSession(sp, context.Logger));

        // The registries outlive Configure, so the bridge can keep republishing into them as the user
        // assembly is swapped by a recompile.
        context.Services.AddSingleton(sp => new UserMenuBridge(
            sp.GetRequiredService<UserMenuCatalog>(), context.Commands, context.Menus, context.Logger));

        context.Services.AddSingleton(sp => new UserSettingsBridge(
            sp.GetRequiredService<UserSettingsCatalog>(), context.Settings, context.Logger));

        context.Services.AddSingleton(sp => new UserPanelBridge(
            sp.GetRequiredService<UserPanelCatalog>(), context.Panels, context.Commands, context.Menus,
            context.Logger));

        context.Services.AddSingleton(sp => new OutputLogBridge(
            sp.GetRequiredService<OutputPanelSettings>(),
            sp.GetRequiredService<PlayModeService>(),
            sp.GetRequiredService<BuildManager>(),
            sp.GetRequiredService<ILogger>()));

        RegisterSettings(context);

        context.Panels.Register(new PanelDescriptor(
            SceneTreePanelId, "Scene Tree", PanelPlacement.Left,
            sp => new SceneTreePanel(
                sp.GetRequiredService<SceneTreeController>(),
                sp.GetRequiredService<NodeInspectorController>(),
                sp.GetRequiredService<AssetManager>())));

        var inspectorInstances = new InspectorInstances(context.Panels, context.TabStripChrome);
        inspectorInstances.RegisterInitial(InspectorPanelId);
        context.Services.AddSingleton(inspectorInstances);

        context.Commands.Register(new CommandDescriptor(
            "gaya.turian.inspector.new", "Window: New Inspector",
            sp => sp.GetRequiredService<IShellHost>().ShowPanel(
                sp.GetRequiredService<InspectorInstances>().CreateAdditional()))
        { MenuLabel = "New Inspector" });
        context.Menus.Add(new MenuItemDescriptor(MenuIds.View, "gaya.turian.inspector.new", "8"));

        context.Panels.Register(new PanelDescriptor(
            ViewportPanelId, "Scene", PanelPlacement.Center,
            sp => new ScenePanel(
                new SceneViewport(
                    sp.GetRequiredService<Vulkan>(),
                    sp.GetRequiredService<SceneTreeController>(),
                    sp.GetRequiredService<NodeInspectorController>(),
                    sp.GetRequiredService<GizmoDrawerCatalog>(),
                    sp.GetRequiredService<PlayModeService>(),
                    sp.GetRequiredService<EditorCameraSettings>(),
                    sp.GetRequiredService<ILogger>()),
                sp.GetRequiredService<SceneTreeController>(),
                sp.GetRequiredService<NodeInspectorController>(),
                sp.GetRequiredService<AssetWorkspace>())));

        context.Panels.Register(new PanelDescriptor(
            "gaya.turian.game", "Game", PanelPlacement.Center,
            sp => new GamePanel(
                new GameViewport(
                    sp.GetRequiredService<Vulkan>(),
                    sp.GetRequiredService<SceneTreeController>(),
                    sp.GetRequiredService<PlayModeService>(),
                    sp.GetRequiredService<ILogger>()))));

        context.Chrome.Register(new ChromeDescriptor(
            "gaya.turian.projectSwitcher", ChromeSlot.MenuBar,
            sp => new ProjectSwitcherChrome(
                sp.GetRequiredService<RecentProjectsSettings>(),
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<ProjectSession>(),
                sp.GetRequiredService<ICommandDispatcher>(),
                sp.GetRequiredService<IEditorSettings>())));
        context.Chrome.Register(new ChromeDescriptor(
            "gaya.turian.playToolbar", ChromeSlot.MenuBar,
            sp => new PlayToolbarChrome(
                sp.GetRequiredService<ICommandDispatcher>(),
                sp.GetRequiredService<PlayModeService>())));

        context.Chrome.Register(new ChromeDescriptor(
            "gaya.turian.documentTabs", ChromeSlot.TopBar,
            sp => new DocumentTabsChrome(
                sp.GetRequiredService<AssetWorkspace>(),
                sp.GetRequiredService<UnsavedChangesDialogChrome>(),
                sp.GetRequiredService<StudioLocalization>()),
            DocumentTabsChrome.Height));

        context.TabStripChrome.Register(new TabStripChromeDescriptor(
            "gaya.turian.output.tabMenu", OutputPanelId,
            sp => new OutputPanelChrome(
                sp.GetRequiredService<OutputPanelSettings>(),
                sp.GetRequiredService<IEditorSettings>())));

        context.TabStripChrome.Register(new TabStripChromeDescriptor(
            "gaya.turian.assets.tabMenu", AssetsPanelId,
            sp => new AssetBrowserChrome(
                sp.GetRequiredService<AssetBrowserSettings>(),
                sp.GetRequiredService<IEditorSettings>())));

        context.Panels.Register(new PanelDescriptor(
            AssetsPanelId, "Assets", PanelPlacement.Bottom,
            sp => new AssetBrowserPanel(
                sp.GetRequiredService<AssetFileSystem>(),
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<AssetOpenService>(),
                sp.GetRequiredService<AssetInspectionService>(),
                sp.GetRequiredService<NodeInspectorController>(),
                sp.GetRequiredService<AssetRevealService>(),
                sp.GetRequiredService<AssetCreationCatalog>(),
                sp.GetRequiredService<AssetBrowserSettings>(),
                sp.GetRequiredService<IEditorSettings>(),
                sp.GetRequiredService<AssetTypeCatalog>(),
                sp.GetRequiredService<AssetPreviewCatalog>())));

        context.Panels.Register(new PanelDescriptor(
            OutputPanelId, "Output", PanelPlacement.Bottom,
            sp => new OutputPanel(
                sp.GetRequiredService<ILogger>(),
                sp.GetRequiredService<OutputPanelSettings>(),
                sp.GetRequiredService<OutputLogBridge>(),
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<IFocusTracker>())));

        context.Panels.Register(new PanelDescriptor(
            SettingsPanelId, "Settings", PanelPlacement.Center,
            sp => new SettingsPanel(
                sp.GetRequiredService<IEditorSettings>(),
                sp.GetRequiredService<ILogger>()))
        { OpenByDefault = false });

        context.Panels.Register(new PanelDescriptor(
            ShortcutsPanelId, "Shortcuts", PanelPlacement.Center,
            sp => new ShortcutsPanel(
                sp.GetRequiredService<IShortcutService>(),
                sp.GetRequiredService<ICommandCatalog>(),
                sp.GetRequiredService<IPanelAccessor>()))
        { OpenByDefault = false });

        // Both dialogs are registered on the menu bar only because that strip renders every frame;
        // neither draws anything into it.
        context.Services.AddSingleton<FileDialogChrome>();
        context.Chrome.Register(new ChromeDescriptor(
            "gaya.turian.fileDialog", ChromeSlot.MenuBar,
            sp => sp.GetRequiredService<FileDialogChrome>()));

        context.Services.AddSingleton<UnsavedChangesDialogChrome>();
        context.Chrome.Register(new ChromeDescriptor(
            "gaya.turian.unsavedChangesDialog", ChromeSlot.MenuBar,
            sp => sp.GetRequiredService<UnsavedChangesDialogChrome>()));

        context.Services.AddSingleton<AboutDialogChrome>();
        context.Chrome.Register(new ChromeDescriptor(
            "gaya.turian.aboutDialog", ChromeSlot.MenuBar,
            sp => sp.GetRequiredService<AboutDialogChrome>()));

        context.Chrome.Register(new ChromeDescriptor(
            "gaya.turian.taskBar", ChromeSlot.StatusBar,
            sp => new TaskBarChrome(sp.GetRequiredService<BackgroundTaskManager>()),
            TaskBarChrome.Height));

        StudioCommands.Register(context);
        PanelCommands.Register(context);
    }

    /// <summary>The Settings panel's id, which the File menu's entry brings to the front.</summary>
    public const string SettingsPanelId = "gaya.turian.settings";

    /// <summary>The keybindings editor's id, which the File menu's entry brings to the front.</summary>
    public const string ShortcutsPanelId = "gaya.turian.shortcuts";

    /// <summary>The Inspector's id, which Project Settings brings to the front.</summary>
    public const string InspectorPanelId = "gaya.turian.inspector";

    /// <summary>The Scene Tree panel's id, which is also the context its shortcuts fire in.</summary>
    public const string SceneTreePanelId = "gaya.turian.sceneTree";

    /// <summary>The Scene viewport's id, which is also the context its shortcuts fire in.</summary>
    public const string ViewportPanelId = "gaya.turian.viewport";

    /// <summary>The Game panel's id and focus context.</summary>
    public const string GamePanelId = "gaya.turian.game";

    /// <summary>The Asset browser's id, which is also the context its shortcuts fire in.</summary>
    public const string AssetsPanelId = "gaya.turian.assets";

    /// <summary>The Output console's id, which is also the context whose tab strip bears its … menu.</summary>
    public const string OutputPanelId = "gaya.turian.output";

    /// <summary>
    /// The studio's own settings pages. Each one is a plain object carrying <c>[EditorSetting]</c>, the
    /// same way user code declares its own, and is registered as a service so whatever the page
    /// configures can read it.
    /// </summary>
    static void RegisterSettings(IPluginContext context)
    {
        var appearance = new AppearanceSettings();
        var camera = new EditorCameraSettings();
        var recent = new RecentProjectsSettings();
        var language = new StudioLanguageSettings();
        var inspector = new InspectorSettings();

        context.Services.AddSingleton(appearance);
        context.Services.AddSingleton(camera);
        context.Services.AddSingleton(recent);
        context.Services.AddSingleton(language);
        context.Services.AddSingleton(inspector);
        var assetBrowser = new AssetBrowserSettings();
        context.Services.AddSingleton(assetBrowser);
        context.Services.AddSingleton(sp => new AppearanceBridge(appearance,
            sp.GetRequiredService<IThemeService>(), sp.GetRequiredService<IEditorSettings>()));
        context.Services.AddSingleton(sp => new LocalizationBridge(language,
            sp.GetRequiredService<StudioLocalization>(), sp.GetRequiredService<IEditorSettings>()));
        var output = new OutputPanelSettings();
        context.Services.AddSingleton(output);

        context.Settings.Register(SettingsPages.Describe(AppearanceBridge.PageId, appearance));
        context.Settings.Register(SettingsPages.Describe(LocalizationBridge.PageId, language));
        context.Settings.Register(SettingsPages.Describe("gaya.turian.editorCamera", camera));
        context.Settings.Register(SettingsPages.Describe(AssetBrowserSettings.PageId, assetBrowser));
        context.Settings.Register(SettingsPages.Describe("gaya.turian.inspector", inspector));
        context.Settings.Register(SettingsPages.Describe("gaya.turian.recentProjects", recent) with
        {
            Path = "Studio/Recent Projects",
            Hidden = true,
        });
        // The Output console's preferences are edited in its own header, so the page only exists for
        // persistence — it never needs an entry in the Settings panel.
        context.Settings.Register(SettingsPages.Describe(OutputPanelSettings.PageId, output) with
        {
            Hidden = true,
        });
    }

    /// <summary>
    /// Advances the play session. It runs in this process, so nothing else moves it forward, and it
    /// must keep running while the Scene panel is behind another dock tab.
    /// </summary>
    /// <param name="services">The built provider.</param>
    /// <param name="deltaTime">Seconds since the previous frame.</param>
    public void Tick(IServiceProvider services, float deltaTime)
    {
        ArgumentNullException.ThrowIfNull(services);

        var playMode = services.GetRequiredService<PlayModeService>();
        if (playMode.IsActive) playMode.Tick(deltaTime);

        services.GetRequiredService<OutputLogBridge>().Tick();
        services.GetRequiredService<UserMenuBridge>().Sync();
        services.GetRequiredService<UserSettingsBridge>().Sync();
        services.GetRequiredService<UserPanelBridge>().Sync();
    }

    /// <inheritdoc />
    public void Stop(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Closing the window cannot be cancelled to ask first, so edits still unsaved then are kept, not lost.
        var workspace = services.GetRequiredService<AssetWorkspace>();
        if (workspace.HasUnsavedChanges)
        {
            services.GetRequiredService<ILogger>().LogInformation("Saving unsaved documents on exit");
            workspace.SaveAll();
        }

        services.GetRequiredService<ProjectSession>().SaveSession();
        services.GetRequiredService<IEditorSettings>().Save();
    }

    string? ArgumentValue(string name)
    {
        var index = args.ToList().IndexOf(name);
        return index >= 0 && index < args.Count - 1 ? args[index + 1] : null;
    }

    /// <inheritdoc />
    public void Start(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Engine code that is not built through DI - components resolving Vulkan or the scene
        // manager from OnAwake - reaches its services through this locator.
        RuntimeServices.Configure(services);

        // BuildManager publishes a static Instance that AssetImporter's constructor requires, so it
        // has to exist before anything that pulls in the scene tree.
        services.GetRequiredService<BuildManager>();
        services.GetRequiredService<SceneDocumentBinder>().Attach();
        services.GetRequiredService<UserMenuBridge>().Sync();
        services.GetRequiredService<UserSettingsBridge>().Sync();
        services.GetRequiredService<UserPanelBridge>().Sync();

        // Resolving the bridge is what starts it: it applies the stored theme and follows the menu.
        services.GetRequiredService<AppearanceBridge>();
        services.GetRequiredService<LocalizationBridge>();

        // Workspace-scoped pages are stored inside the project, so they can only be restored once one
        // is open — and re-restored when the user opens another.
        var editorSettings = services.GetRequiredService<IEditorSettings>();
        services.GetRequiredService<SettingsService>().SettingsLoaded +=
            project =>
            {
                editorSettings.BindWorkspace(project.ProjectAbsoluteDir);
                var recent = services.GetRequiredService<RecentProjectsSettings>();
                recent.Add(project.ProjectAbsoluteDir);
                editorSettings.NotifyChanged("gaya.turian.recentProjects");
            };

        var projectPath = ProjectBootstrapper.ParseProjectArgument([.. args]);
        if (projectPath is null)
        {
            log.LogInformation("No --project given; start with one to open a scene");
            return;
        }

        services.GetRequiredService<ProjectSession>().Open(projectPath, ArgumentValue("--scene"));
    }
}
