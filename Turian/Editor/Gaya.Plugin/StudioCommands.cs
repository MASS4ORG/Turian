namespace Gaya.Plugin.Turian;

/// <summary>
/// The studio's menu commands. Each one resolves what it needs from the provider and delegates to
/// the framework-agnostic controllers in <c>Editor.Core</c>, so the Avalonia shell and this one drive
/// identical behavior.
/// </summary>
static class StudioCommands
{
    internal const string OpenCSharpProjectId = "gaya.turian.openCSharpProject";
    internal const string OpenCSharpProjectLabel = "Open C# Project";

    /// <summary>Registers the project test runner command and its menu entry.</summary>
    /// <param name="context">The plugin registration surface.</param>
    public static void RegisterTests(IPluginContext context) =>
        Add(context, MenuIds.Project, "3", 9, new CommandDescriptor(
                "gaya.turian.tests", "Run: Test Runner…",
                static sp => sp.GetRequiredService<IShellHost>().ShowPanel(GayaPlugin.TestRunnerPanelId), HasProject)
        { DynamicLabel = Localized("Test Runner…") });

    /// <summary>Registers every command, its menu entry and its key binding.</summary>
    /// <param name="context">The registration surface handed to the plugin.</param>
    public static void Register(IPluginContext context)
    {
        RegisterFile(context);
        RegisterEdit(context);
        RegisterSolution(context);
        RegisterAssets(context);
        RegisterPlay(context);
        RegisterPlayControls(context);
        RegisterExport(context);
        RegisterHelp(context);
    }

    static void RegisterFile(IPluginContext context)
    {
        Add(context, MenuIds.File, "1", 0, new CommandDescriptor(
            "gaya.turian.newProject", "File: New Project…",
            NewProject)
        { DynamicLabel = Localized("New Project…") });
        Add(context, MenuIds.File, "1", 1, new CommandDescriptor(
            "gaya.turian.openProject", "File: Open Project…",
            OpenProject)
        { DynamicLabel = Localized("Open…") });

        Add(context, MenuIds.File, "2-save", 2, new CommandDescriptor(
            "gaya.turian.save", "File: Save",
            Save,
            static sp => sp.GetRequiredService<AssetWorkspace>().Active is not null
                || sp.GetRequiredService<AssetDocumentHost>().Active is not null)
        { DynamicLabel = Localized("Save") });
        context.Shortcuts.Add(new KeyBinding("gaya.turian.save", KeyboardKey.S, KeyModifiers.Ctrl));
        Add(context, MenuIds.File, "2-save", 3, new CommandDescriptor(
            "gaya.turian.saveAll", "File: Save All",
            SaveAll,
            static sp => sp.GetRequiredService<AssetWorkspace>().Documents.Count > 0
                || sp.GetRequiredService<AssetDocumentHost>().Sessions.Count > 0)
        { DynamicLabel = Localized("Save All") });
        context.Shortcuts.Add(new KeyBinding("gaya.turian.saveAll", KeyboardKey.S,
            KeyModifiers.Ctrl | KeyModifiers.Shift));

        Add(context, MenuIds.File, "4", 0, new CommandDescriptor(
            "gaya.turian.exit", "File: Exit",
            Exit)
        { DynamicLabel = Localized("Exit") });
    }

    static void RegisterEdit(IPluginContext context)
    {
        Add(context, MenuIds.Edit, "0", 0, new CommandDescriptor(
            "gaya.turian.undo", "Edit: Undo",
            static sp =>
            {
                if (!sp.GetRequiredService<AssetDocumentHost>().Undo())
                {
                    sp.GetRequiredService<UndoService>().Undo();
                }
            },
            static sp => sp.GetRequiredService<AssetDocumentHost>().Active is not null
                ? sp.GetRequiredService<AssetDocumentHost>().CanUndo
                : sp.GetRequiredService<UndoService>().CanUndo)
        {
            DynamicLabel = static sp => StepLabel(sp, "Undo", sp.GetRequiredService<AssetDocumentHost>().Active is null
            ? sp.GetRequiredService<UndoService>().UndoLabel : null)
        });
        context.Shortcuts.Add(new KeyBinding("gaya.turian.undo", KeyboardKey.Z, KeyModifiers.Ctrl));
        Add(context, MenuIds.Edit, "0", 1, new CommandDescriptor(
            "gaya.turian.redo", "Edit: Redo",
            static sp =>
            {
                if (!sp.GetRequiredService<AssetDocumentHost>().Redo())
                {
                    sp.GetRequiredService<UndoService>().Redo();
                }
            },
            static sp => sp.GetRequiredService<AssetDocumentHost>().Active is not null
                ? sp.GetRequiredService<AssetDocumentHost>().CanRedo
                : sp.GetRequiredService<UndoService>().CanRedo)
        {
            DynamicLabel = static sp => StepLabel(sp, "Redo", sp.GetRequiredService<AssetDocumentHost>().Active is null
            ? sp.GetRequiredService<UndoService>().RedoLabel : null)
        });
        context.Shortcuts.Add(new KeyBinding("gaya.turian.redo", KeyboardKey.Z, KeyModifiers.Ctrl | KeyModifiers.Shift));
        context.Shortcuts.Add(new KeyBinding("gaya.turian.redo", KeyboardKey.Y, KeyModifiers.Ctrl));

        Add(context, MenuIds.Edit, "2-settings", 1, new CommandDescriptor(
            "gaya.turian.keyboardShortcuts", "Edit: Keyboard Shortcuts…",
            static sp => sp.GetRequiredService<IShellHost>().ShowPanel(GayaPlugin.ShortcutsPanelId))
        { DynamicLabel = Localized("Keyboard Shortcuts…") });
        context.Shortcuts.Add(new KeyBinding("gaya.turian.keyboardShortcuts",
            new KeyStroke(KeyboardKey.K, KeyModifiers.Ctrl), new KeyStroke(KeyboardKey.S, KeyModifiers.Ctrl)));
    }

    static void RegisterSolution(IPluginContext context)
    {
        Add(context, MenuIds.Project, "0-solution", 0, new CommandDescriptor(
            "gaya.turian.regenerateCSharpSolution", "Project: Regenerate C# Solution",
            RegenerateCSharpSolution,
            HasProject)
        { DynamicLabel = Localized("Regenerate C# Solution") });
        Add(context, MenuIds.Project, "0-solution", 1, new CommandDescriptor(
            OpenCSharpProjectId, "Project: " + OpenCSharpProjectLabel,
            OpenCSharpProject,
            HasProject)
        { DynamicLabel = Localized(OpenCSharpProjectLabel) });
        context.Shortcuts.Add(new KeyBinding(OpenCSharpProjectId, KeyboardKey.O,
            KeyModifiers.Ctrl | KeyModifiers.Shift));
    }

    static void RegisterAssets(IPluginContext context)
    {
        Add(context, MenuIds.Project, "0-solution", 2, new CommandDescriptor(
            "gaya.turian.recompile", "Assets: Recompile Scripts",
            static sp => sp.GetRequiredService<ProjectSession>().RecompileScripts(),
            HasProject)
        { DynamicLabel = Localized("Recompile") });
        Add(context, MenuIds.Project, "0-solution", 3, new CommandDescriptor(
            "gaya.turian.reimportAssets", "Assets: Reimport All",
            Reimport,
            HasProject)
        { DynamicLabel = Localized("Reimport Assets") });
    }

    static void RegisterPlay(IPluginContext context)
    {
        Add(context, MenuIds.Project, "3", 2, new CommandDescriptor(
            "gaya.turian.play", "Run: Play",
            TogglePlay,
            static sp => sp.GetRequiredService<PlayModeService>().IsActive
                  || sp.GetRequiredService<SceneTreeController>().CurrentSceneRoot is not null)
        { DynamicLabel = static sp => sp.GetRequiredService<PlayModeService>().IsActive ? "Stop" : "Play" });
        Add(context, MenuIds.Project, "3", 3, new CommandDescriptor(
            "gaya.turian.playStartupScene", "Run: Play Startup Scene",
            PlayStartupScene,
            HasProject)
        { DynamicLabel = Localized("Play Startup Scene") });

        context.Shortcuts.Add(new KeyBinding("gaya.turian.play", KeyboardKey.F5));
        context.Shortcuts.Add(new KeyBinding("gaya.turian.playPause", KeyboardKey.F6));
        context.Shortcuts.Add(new KeyBinding("gaya.turian.playStep", KeyboardKey.F10));
    }

    static void RegisterPlayControls(IPluginContext context)
    {
        Add(context, MenuIds.Project, "3", 4, new CommandDescriptor(
            "gaya.turian.playPause", "Run: Pause / Resume",
            TogglePause,
            static sp => sp.GetRequiredService<PlayModeService>().IsActive)
        {
            DynamicLabel = static sp =>
            sp.GetRequiredService<PlayModeService>().State == PlayState.Paused ? "Resume" : "Pause"
        });

        Add(context, MenuIds.Project, "3", 5, new CommandDescriptor(
            "gaya.turian.playStep", "Run: Step Frame",
            static sp => sp.GetRequiredService<PlayModeService>().StepFrame(),
            static sp => sp.GetRequiredService<PlayModeService>().State == PlayState.Paused)
        { DynamicLabel = Localized("Step Frame") });
    }

    static void RegisterExport(IPluginContext context)
    {
        Add(context, MenuIds.Project, "export", 0, new CommandDescriptor(
                "gaya.turian.buildProfiles", "Run: Build Profiles…",
                static sp => sp.GetRequiredService<IShellHost>().ShowPanel(GayaPlugin.BuildProfilesPanelId),
                HasProject)
        { DynamicLabel = Localized("Build Profiles…") });
        context.Shortcuts.Add(new KeyBinding("gaya.turian.buildProfiles", KeyboardKey.B,
            KeyModifiers.Ctrl | KeyModifiers.Shift));
        Add(context, MenuIds.Project, "export", 1, new CommandDescriptor(
            "gaya.turian.buildAndRun", "Run: Build and Run",
            ToggleStandalone,
            HasProject)
        {
            DynamicLabel = static sp =>
            sp.GetRequiredService<BuildManager>().IsPlaying ? "Stop Standalone" : "Build & Run"
        });
        Add(context, MenuIds.Project, "export", 2, new CommandDescriptor(
                "gaya.turian.export", "Run: Export",
                Export,
                HasProject)
        { DynamicLabel = Localized("Export") });
    }

    static void RegisterHelp(IPluginContext context)
    {
        Add(context, MenuIds.Help, "1", 0, new CommandDescriptor(
            "gaya.turian.about", "Help: About",
            static sp => sp.GetRequiredService<AboutDialogChrome>().Open())
        { DynamicLabel = Localized("About") });
    }

    /// <summary>"Undo Rename" or plain "Undo" when there is nothing to name.</summary>
    static string StepLabel(IServiceProvider services, string action, string? step)
    {
        var localization = services.GetRequiredService<StudioLocalization>();
        return step is null ? localization.T(action) : $"{localization.T(action)} {step}";
    }

    static Func<IServiceProvider, string> Localized(string text) =>
        services => services.GetRequiredService<StudioLocalization>().T(text);

    static void Add(IPluginContext context, string menuId, string group, int order,
        CommandDescriptor command, string path = "")
    {
        context.Commands.Register(command);
        context.Menus.Add(new MenuItemDescriptor(menuId, command.Id, group, order, path));
    }

    /// <summary>
    /// Shows the project's settings asset of one kind in the inspector and the asset browser. A project
    /// that lists none gets one written with the kind's defaults, the way Flax creates a missing
    /// settings asset.
    /// </summary>
    internal static void OpenProjectSettings(IServiceProvider services, Type type)
    {
        if (services.GetRequiredService<SettingsService>().Settings is not { } project)
        {
            return;
        }

        var existing = ProjectSettingsFiles.Locate(project, type);
        var path = existing ?? ProjectSettingsFiles.Ensure(project, type);
        if (existing is null)
        {
            services.GetRequiredService<AssetImporter>().ReimportNow(path);
        }

        var inspection = services.GetRequiredService<AssetInspectionService>().Inspect(path);
        services.GetRequiredService<NodeInspectorController>().Select(inspection);
        if (inspection is not null)
        {
            services.GetRequiredService<AssetRevealService>().Reveal(inspection.Metadata.Id);
        }

        services.GetRequiredService<IShellHost>().ShowPanel(GayaPlugin.InspectorPanelId);
    }

    /// <summary>Exits, first asking whether to save documents with unsaved edits.</summary>
    static void Exit(IServiceProvider services) =>
        services.GetRequiredService<UnsavedChangesGuard>().Leave(
            "Some documents have unsaved changes. Save them before exiting?",
            services.GetRequiredService<IShellHost>().RequestExit);

    const string leaveProjectQuestion = "Some documents have unsaved changes. Save them before leaving the project?";

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
                if (path is not null)
                {
                    services.GetRequiredService<UnsavedChangesGuard>().Leave(leaveProjectQuestion,
                        () => session.Create(path));
                }
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
                if (path is not null)
                {
                    services.GetRequiredService<UnsavedChangesGuard>().Leave(leaveProjectQuestion,
                        () => session.QueueOpen(path));
                }
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

    /// <summary>Registers temporary scene creation in the File menu and the global keyboard shortcuts.</summary>
    /// <param name="context">The plugin's command, menu and shortcut registries.</param>
    public static void RegisterNewScene(IPluginContext context)
    {
        Add(context, MenuIds.File, "1", -1, new CommandDescriptor(
            "gaya.turian.newScene", "File: New Scene",
            static sp => sp.GetRequiredService<AssetWorkspace>().NewScene(),
            static sp => !sp.GetRequiredService<PlayModeService>().IsActive)
        { DynamicLabel = Localized("New Scene") });
        context.Shortcuts.Add(new KeyBinding("gaya.turian.newScene", KeyboardKey.N, KeyModifiers.Ctrl));
    }

    static void SaveAll(IServiceProvider services)
    {
        services.GetRequiredService<AssetWorkspace>().SaveAll();
        services.GetRequiredService<AssetDocumentHost>().SaveAll();
    }

    static void Save(IServiceProvider services)
    {
        if (services.GetRequiredService<AssetDocumentHost>().SaveActive() is not null)
        {
            return;
        }

        var workspace = services.GetRequiredService<AssetWorkspace>();
        if (workspace.Active is { } document)
        {
            workspace.Save(document);
        }
    }

    /// <summary>
    /// Rewrites every <c>.meta</c> under the project's assets folder, which is what re-runs the import
    /// pipelines. Held as a background task so the status bar shows it and the asset lock is taken.
    /// </summary>
    static void Reimport(IServiceProvider services)
    {
        var settings = services.GetRequiredService<SettingsService>().Settings;
        if (settings is null)
        {
            return;
        }

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
    /// Rewrites the generated projects and solution an external IDE opens, as a project-locked task so it cannot
    /// race a compile, and logs where they are.
    /// </summary>
    static void RegenerateCSharpSolution(IServiceProvider services)
    {
        var settings = services.GetRequiredService<SettingsService>().Settings;
        if (settings is null)
        {
            return;
        }

        var log = services.GetRequiredService<ILogger>();
        services.GetRequiredService<BackgroundTaskRunner>().Run(
            new BackgroundTaskSpec
            {
                Label = "Regenerate project files",
                Kind = BackgroundTaskKind.Build,
                Locks = EditorLocks.Project,
                Policy = DuplicatePolicy.Drop,
            },
            (progress, _) =>
            {
                progress.Report(0, "Generating the projects");
                var files = ProjectRegeneration.Regenerate(settings, log);
                log.LogInformation("{State}: {Solution}",
                    files.Changed ? "Project files regenerated" : "Project files up to date", files.Solution);
                progress.Report(1, files.Solution);
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Regenerates the C# solution and opens it in the external editor, as a project-locked task so it cannot race a
    /// compile. A failed regeneration opens nothing and the task ends failed.
    /// </summary>
    static void OpenCSharpProject(IServiceProvider services)
    {
        var code = services.GetRequiredService<CodeOpener>();
        services.GetRequiredService<BackgroundTaskRunner>().RunWithResult(
            new BackgroundTaskSpec
            {
                Label = "Open C# project",
                Kind = BackgroundTaskKind.Build,
                Locks = EditorLocks.Project,
                Policy = DuplicatePolicy.Drop,
            },
            async (progress, cancellation) =>
            {
                progress.Report(0, "Generating the projects");
                var opened = await code.OpenProjectAsync(cancellation).ConfigureAwait(false);
                return new BackgroundTaskResult(opened, opened ? string.Empty : "The C# project was not opened");
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
        {
            services.GetRequiredService<ILogger>().LogWarning("Cannot play: open a scene first");
        }
    }

    static void PlayStartupScene(IServiceProvider services)
    {
        var playMode = services.GetRequiredService<PlayModeService>();
        if (playMode.IsActive)
        {
            return;
        }

        if (!services.GetRequiredService<ProjectSession>().OpenStartupScene())
        {
            return;
        }

        FocusGamePanel(services);
        services.GetRequiredService<AssetManager>().SaveAllAssets();
        if (!playMode.Start())
        {
            services.GetRequiredService<ILogger>().LogWarning("Cannot play the startup scene");
        }
    }

    static void FocusGamePanel(IServiceProvider services)
    {
        services.GetRequiredService<IShellHost>().ShowPanel(GayaPlugin.GamePanelId);
        services.GetRequiredService<IFocusTracker>().Focus(GayaPlugin.GamePanelId);
    }

    static void TogglePause(IServiceProvider services)
    {
        var playMode = services.GetRequiredService<PlayModeService>();
        if (playMode.State == PlayState.Playing)
        {
            playMode.Pause();
        }
        else
        {
            playMode.Resume();
        }
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

        // A device platform runs the game on the device: export, install and start it there.
        if (build.ActiveProfileDeploys)
        {
            RunExport(services.GetRequiredService<BackgroundTaskRunner>(), () => build.ExportAsync(install: true));
            return;
        }

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
        RunExport(services.GetRequiredService<BackgroundTaskRunner>(), () => build.ExportAsync());
    }

    /// <summary>Runs an export as a blocking background task that the status bar shows.</summary>
    /// <param name="runner">The studio's background task runner.</param>
    /// <param name="export">Starts the export.</param>
    public static void RunExport(BackgroundTaskRunner runner, Func<Task<BuildTaskStatus>> export)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(export);

        runner.Run(
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
                var status = await export().ConfigureAwait(false);
                if (status.State != BuildTaskState.Succeeded)
                {
                    throw new InvalidOperationException(status.Message);
                }

                progress.Report(1, status.Message);
            });
    }
}
