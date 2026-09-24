namespace Gaya.Plugin.Turian;

/// <summary>
/// The studio's menu commands. Each one resolves what it needs from the provider and delegates to
/// the framework-agnostic controllers in <c>Editor.Core</c>, so the Avalonia shell and this one drive
/// identical behavior.
/// </summary>
static class StudioCommands
{
    /// <summary>Registers every command, its menu entry and its key binding.</summary>
    /// <param name="context">The registration surface handed to the plugin.</param>
    public static void Register(IPluginContext context)
    {
        // ── Edit: the shell's own language ──────────────────────────────────
        // The chosen language is written back into the Language settings page by the bridge, so both
        // the menu and the Settings panel drive the same stored value.
        Add(context, MenuIds.Edit, "1", 0, new CommandDescriptor(
            "gaya.turian.locale.english", "Edit: Localization: English",
            sp => sp.GetRequiredService<StudioLocalization>().SetLocale("en"))
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("English") }, "Localization");

        Add(context, MenuIds.Edit, "1", 1, new CommandDescriptor(
            "gaya.turian.locale.ptBR", "Edit: Localization: Português (Brasil)",
            sp => sp.GetRequiredService<StudioLocalization>().SetLocale("pt-BR"))
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Português (Brasil)") }, "Localization");

        // ── File: project and documents ─────────────────────────────────────
        Add(context, MenuIds.File, "1", 0, new CommandDescriptor(
            "gaya.turian.newProject", "File: New Project…",
            NewProject)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("New Project…") });

        Add(context, MenuIds.File, "1", 1, new CommandDescriptor(
            "gaya.turian.openProject", "File: Open Project…",
            OpenProject)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Open…") });

        Add(context, MenuIds.File, "1", 2, new CommandDescriptor(
            "gaya.turian.save", "File: Save",
            Save,
            sp => sp.GetRequiredService<AssetWorkspace>().Active is not null)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Save") });

        Add(context, MenuIds.File, "1", 3, new CommandDescriptor(
            "gaya.turian.saveAll", "File: Save All",
            sp => sp.GetRequiredService<AssetWorkspace>().SaveAll(),
            sp => sp.GetRequiredService<AssetWorkspace>().Documents.Count > 0)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Save All") });

        context.Shortcuts.Add(new KeyBinding("gaya.turian.save", KeyboardKey.S, KeyModifiers.Ctrl));
        context.Shortcuts.Add(new KeyBinding("gaya.turian.saveAll", KeyboardKey.S,
            KeyModifiers.Ctrl | KeyModifiers.Shift));

        // ── File: settings ──────────────────────────────────────────────────
        Add(context, MenuIds.File, "2", 0, new CommandDescriptor(
            "gaya.turian.settings", "File: Settings…",
            sp => sp.GetRequiredService<IShellHost>().ShowPanel(GayaPlugin.SettingsPanelId))
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Settings…") });

        context.Shortcuts.Add(new KeyBinding("gaya.turian.settings", KeyboardKey.Comma, KeyModifiers.Ctrl));

        Add(context, MenuIds.File, "2", 1, new CommandDescriptor(
            "gaya.turian.keyboardShortcuts", "File: Keyboard Shortcuts…",
            sp => sp.GetRequiredService<IShellHost>().ShowPanel(GayaPlugin.ShortcutsPanelId))
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Keyboard Shortcuts…") });

        // A chord, so the studio ships with one: Ctrl+K arms it and Ctrl+S completes it.
        context.Shortcuts.Add(new KeyBinding("gaya.turian.keyboardShortcuts",
            new KeyStroke(KeyboardKey.K, KeyModifiers.Ctrl), new KeyStroke(KeyboardKey.S, KeyModifiers.Ctrl)));

        // ── File: exit ──────────────────────────────────────────────────────
        Add(context, MenuIds.File, "4", 0, new CommandDescriptor(
            "gaya.turian.exit", "File: Exit",
            Exit)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Exit") });

        // ── Project ─────────────────────────────────────────────
        Add(context, MenuIds.Project, "3", 0, new CommandDescriptor(
            "gaya.turian.recompile", "Assets: Recompile Scripts",
            sp => sp.GetRequiredService<ProjectSession>().RecompileScripts(),
            HasProject)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Recompile") });

        Add(context, MenuIds.Project, "3", 1, new CommandDescriptor(
            "gaya.turian.reimportAssets", "Assets: Reimport All",
            Reimport,
            HasProject)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Reimport Assets") });

        Add(context, MenuIds.Project, "3", 2, new CommandDescriptor(
            "gaya.turian.play", "Run: Play",
            TogglePlay,
            sp => sp.GetRequiredService<PlayModeService>().IsActive
                  || sp.GetRequiredService<SceneTreeController>().CurrentSceneRoot is not null)
        { DynamicLabel = sp => sp.GetRequiredService<PlayModeService>().IsActive ? "Stop" : "Play" });

        Add(context, MenuIds.Project, "3", 3, new CommandDescriptor(
            "gaya.turian.playStartupScene", "Run: Play Startup Scene",
            PlayStartupScene,
            HasProject)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Play Startup Scene") });

        context.Shortcuts.Add(new KeyBinding("gaya.turian.play", KeyboardKey.F5));
        context.Shortcuts.Add(new KeyBinding("gaya.turian.playPause", KeyboardKey.F6));
        context.Shortcuts.Add(new KeyBinding("gaya.turian.playStep", KeyboardKey.F10));

        Add(context, MenuIds.Project, "3", 4, new CommandDescriptor(
            "gaya.turian.playPause", "Run: Pause / Resume",
            TogglePause,
            sp => sp.GetRequiredService<PlayModeService>().IsActive)
        {
            DynamicLabel = sp =>
            sp.GetRequiredService<PlayModeService>().State == PlayState.Paused ? "Resume" : "Pause"
        });

        Add(context, MenuIds.Project, "3", 5, new CommandDescriptor(
            "gaya.turian.playStep", "Run: Step Frame",
            sp => sp.GetRequiredService<PlayModeService>().StepFrame(),
            sp => sp.GetRequiredService<PlayModeService>().State == PlayState.Paused)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Step Frame") });

        Add(context, MenuIds.Project, "3", 6, new CommandDescriptor(
            "gaya.turian.buildAndRun", "Run: Build and Run",
            ToggleStandalone,
            HasProject)
        {
            DynamicLabel = sp =>
            sp.GetRequiredService<BuildManager>().IsPlaying ? "Stop Standalone" : "Build & Run"
        });

        Add(context, MenuIds.Project, "3", 7, new CommandDescriptor(
                "gaya.turian.export", "Run: Export",
                Export,
                HasProject)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("Export") });

        AddProjectSettings<PlayerSettings>(context, "player", "Player", 0);
        AddProjectSettings<InputSettings>(context, "input", "Input", 1);
        AddProjectSettings<GraphicsSettings>(context, "graphics", "Graphics", 2);

        // ── Help ────────────────────────────────────────────────────────────
        Add(context, MenuIds.Help, "1", 0, new CommandDescriptor(
            "gaya.turian.about", "Help: About",
            sp => sp.GetRequiredService<AboutDialogChrome>().Open())
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T("About") });
    }

    static void Add(IPluginContext context, string menuId, string group, int order,
        CommandDescriptor command, string path = "")
    {
        context.Commands.Register(command);
        context.Menus.Add(new MenuItemDescriptor(menuId, command.Id, group, order, path));
    }

    /// <summary>A Project / Settings entry for one settings kind.</summary>
    static void AddProjectSettings<T>(IPluginContext context, string id, string label, int order)
        where T : ProjectSettingsAsset
    {
        var command = new CommandDescriptor(
            $"gaya.turian.projectSettings.{id}", $"Settings: {label}",
            sp => OpenProjectSettings(sp, typeof(T)),
            HasProject)
        { DynamicLabel = sp => sp.GetRequiredService<StudioLocalization>().T(label) };

        context.Commands.Register(command);
        context.Menus.Add(new MenuItemDescriptor(MenuIds.Project, command.Id, "2", 2 + order, "Project Settings"));
    }

    /// <summary>
    /// Shows the project's settings asset of one kind in the inspector and the asset browser. A project
    /// that lists none gets one written with the kind's defaults, the way Flax creates a missing
    /// settings asset.
    /// </summary>
    static void OpenProjectSettings(IServiceProvider services, Type type)
    {
        if (services.GetRequiredService<SettingsService>().Settings is not { } project) return;

        var existing = ProjectSettingsFiles.Locate(project, type);
        var path = existing ?? ProjectSettingsFiles.Ensure(project, type);
        if (existing is null) services.GetRequiredService<AssetImporter>().ReimportNow(path);

        var inspection = services.GetRequiredService<AssetInspectionService>().Inspect(path);
        services.GetRequiredService<NodeInspectorController>().Select(inspection);
        if (inspection is not null) services.GetRequiredService<AssetRevealService>().Reveal(inspection.Metadata.Id);

        services.GetRequiredService<IShellHost>().ShowPanel(GayaPlugin.InspectorPanelId);
    }

    /// <summary>Exits, first asking whether to save documents with unsaved edits.</summary>
    static void Exit(IServiceProvider services)
    {
        var shell = services.GetRequiredService<IShellHost>();
        var workspace = services.GetRequiredService<AssetWorkspace>();
        if (!workspace.HasUnsavedChanges)
        {
            shell.RequestExit();
            return;
        }

        var question = services.GetRequiredService<StudioLocalization>()
            .T("Some documents have unsaved changes. Save them before exiting?");
        services.GetRequiredService<UnsavedChangesDialogChrome>().Ask(question, choice =>
        {
            if (choice == UnsavedChanges.Save) workspace.SaveAll();
            else workspace.CloseAll();
            shell.RequestExit();
        });
    }

    static bool HasProject(IServiceProvider services) =>
        services.GetRequiredService<SettingsService>().Settings is not null;

    /// <summary>
    /// Asks for a folder to scaffold a project into, then creates and opens it. The folder may be a
    /// new one typed into the dialog's name field or an existing empty one.
    /// </summary>
    static void NewProject(IServiceProvider services)
    {
        var session = services.GetRequiredService<ProjectSession>();
        var localization = services.GetRequiredService<StudioLocalization>();

        services.GetRequiredService<FileDialogChrome>().Show(new FileDialogRequest
        {
            Mode = FileDialogMode.CreateFolder,
            Title = localization.T("New Project"),
            StartPath = ProjectsRoot(services),
            InitialName = "New Project",
            ConfirmLabel = localization.T("Create"),
            Validate = static path => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()
                ? $"{Path.GetFileName(path)} already exists and is not empty."
                : null,
            OnComplete = path =>
            {
                if (path is not null) session.Create(path);
            },
        });
    }

    /// <summary>Asks for a project folder and opens it, refusing one that is not a project.</summary>
    static void OpenProject(IServiceProvider services)
    {
        var session = services.GetRequiredService<ProjectSession>();
        var localization = services.GetRequiredService<StudioLocalization>();

        services.GetRequiredService<FileDialogChrome>().Show(new FileDialogRequest
        {
            Mode = FileDialogMode.SelectFolder,
            Title = localization.T("Open Project"),
            StartPath = ProjectsRoot(services),
            ConfirmLabel = localization.T("Open"),

            // The dialog says so before closing, rather than opening the folder and logging a failure
            // the user has to go and find in the output panel.
            Validate = static path => ProjectValidator.Validate(path)
                .FirstOrDefault(issue => issue.Severity == ProjectIssueSeverity.Error)?.Message,

            OnComplete = path =>
            {
                if (path is not null) session.Open(path);
            },
        });
    }

    /// <summary>
    /// Where a project dialog starts: beside the open project, since projects are usually kept
    /// together, and the user's home when none is open.
    /// </summary>
    static string? ProjectsRoot(IServiceProvider services) =>
        services.GetRequiredService<SettingsService>().Settings?.ProjectAbsoluteDir is { } project
            ? Path.GetDirectoryName(project)
            : null;

    static void Save(IServiceProvider services)
    {
        var workspace = services.GetRequiredService<AssetWorkspace>();
        if (workspace.Active is { } document) workspace.Save(document);
    }

    /// <summary>
    /// Rewrites every <c>.meta</c> under the project's assets folder, which is what re-runs the import
    /// pipelines. Held as a background task so the status bar shows it and the asset lock is taken.
    /// </summary>
    static void Reimport(IServiceProvider services)
    {
        var settings = services.GetRequiredService<SettingsService>().Settings;
        if (settings is null) return;

        var importer = services.GetRequiredService<AssetImporter>();

        services.GetRequiredService<BackgroundTaskRunner>().Run(
            new BackgroundTaskSpec
            {
                Label = "Reimport assets",
                Kind = BackgroundTaskKind.Import,
                Locks = EditorLocks.Assets,
                BlocksUi = true,
                Policy = DuplicatePolicy.Drop,
            },
            async (progress, _) =>
            {
                progress.Report(0, settings.AssetsAbsoluteDir);
                await importer.GenerateMetaFilesAsync(settings.AssetsAbsoluteDir).ConfigureAwait(false);
                progress.Report(1, "Reimport complete");
            });
    }

    /// <summary>
    /// Starts or stops the in-process session. Edits are written first so the copy the session runs
    /// loads the same assets that are on disk.
    /// </summary>
    static void TogglePlay(IServiceProvider services)
    {
        FocusGamePanel(services);
        var playMode = services.GetRequiredService<PlayModeService>();
        if (playMode.IsActive)
        {
            playMode.Stop();
            return;
        }

        services.GetRequiredService<AssetManager>().SaveAllAssets();

        if (!playMode.Start())
            services.GetRequiredService<ILogger>().LogWarning("Cannot play: open a scene first");
    }

    static void PlayStartupScene(IServiceProvider services)
    {
        var playMode = services.GetRequiredService<PlayModeService>();
        if (playMode.IsActive) return;

        if (!services.GetRequiredService<ProjectSession>().OpenStartupScene()) return;

        FocusGamePanel(services);
        services.GetRequiredService<AssetManager>().SaveAllAssets();
        if (!playMode.Start())
            services.GetRequiredService<ILogger>().LogWarning("Cannot play the startup scene");
    }

    static void FocusGamePanel(IServiceProvider services)
    {
        services.GetRequiredService<IShellHost>().ShowPanel(GayaPlugin.GamePanelId);
        services.GetRequiredService<IFocusTracker>().Focus(GayaPlugin.GamePanelId);
    }

    static void TogglePause(IServiceProvider services)
    {
        var playMode = services.GetRequiredService<PlayModeService>();
        if (playMode.State == PlayState.Playing) playMode.Pause();
        else playMode.Resume();
    }

    /// <summary>The out-of-process path: builds the standalone runtime and launches it in its own window.</summary>
    static void ToggleStandalone(IServiceProvider services)
    {
        var build = services.GetRequiredService<BuildManager>();
        if (build.IsPlaying)
        {
            build.StopPlay();
            return;
        }

        services.GetRequiredService<AssetManager>().SaveAllAssets();

        services.GetRequiredService<BackgroundTaskRunner>().Run(
            new BackgroundTaskSpec
            {
                Label = "Build & run",
                Kind = BackgroundTaskKind.Build,
                Locks = EditorLocks.Project,
                BlocksUi = true,
                Policy = DuplicatePolicy.Drop,
            },
            async (progress, _) =>
            {
                progress.Report(0, "Building the standalone runtime");
                await build.PlayAsync().ConfigureAwait(false);
                progress.Report(1, "Standalone launched");
            });
    }

    static void Export(IServiceProvider services)
    {
        var build = services.GetRequiredService<BuildManager>();

        services.GetRequiredService<BackgroundTaskRunner>().Run(
            new BackgroundTaskSpec
            {
                Label = "Export",
                Kind = BackgroundTaskKind.Package,
                Locks = EditorLocks.Project,
                BlocksUi = true,
                Policy = DuplicatePolicy.Drop,
            },
            async (progress, _) =>
            {
                progress.Report(0, "Packaging the project");
                var status = await build.ExportAsync().ConfigureAwait(false);
                if (status.State != BuildTaskState.Succeeded)
                    throw new InvalidOperationException(status.Message);

                progress.Report(1, status.Message);
            });
    }
}
