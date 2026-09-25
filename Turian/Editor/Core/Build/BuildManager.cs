namespace Turian.Editor.Core;

/// <summary>
/// Central coordinator for all build operations: compilation, play, export, hot-reload.
///
/// <para>
/// Consumers (UI or CLI) interact via the public async methods and subscribe to the
/// events exposed by <see cref="TaskRunner"/> and the play-process lifecycle events.
/// </para>
/// </summary>
public sealed class BuildManager : IDisposable
{
    // ── Singleton ──────────────────────────────────────────────────────────────

    static BuildManager? instance;

    /// <summary>Returns the singleton instance, or throws if not yet initialised.</summary>
    public static BuildManager Instance =>
        instance ?? throw new InvalidOperationException("BuildManager not initialized.");

    // ── Fields ─────────────────────────────────────────────────────────────────

    BuildAppSettings settings;
    readonly ILogger logger;
    AssemblySlotManager slotManager;
    readonly SourceFileWatcher sourceWatcher;
    readonly object playProcessLock = new();
    bool studioIsActive = true;
    bool isPlaying;
    bool pendingHotReload;
    Process? playProcess;
    bool disposed;

    // ── Public surface ─────────────────────────────────────────────────────────

    /// <summary>
    /// Background task runner.  Subscribe to <see cref="BuildTaskRunner.TaskStarted"/>,
    /// <see cref="BuildTaskRunner.TaskCompleted"/>, and <see cref="BuildTaskRunner.UiLockChanged"/>
    /// to receive build notifications.
    /// </summary>
    public BuildTaskRunner TaskRunner { get; }

    // ── Play lifecycle events ──────────────────────────────────────────────────

    /// <summary>Raised when a play process starts successfully.</summary>
    public event Action<string>? OnPlayStarted;

    /// <summary>Raised when a play process fails to start.</summary>
    public event Action<string>? OnPlayFailure;

    /// <summary>Raised when a play process stops (either by request or natural exit).</summary>
    public event Action<string>? OnPlayStopped;

    // ── Constructor ────────────────────────────────────────────────────────────

    /// <param name="settings">Initial application settings.</param>
    /// <param name="logger">The logger.</param>
    public BuildManager(IAppSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
        TaskRunner = new BuildTaskRunner(logger);
        sourceWatcher = new SourceFileWatcher(logger);

        if (string.IsNullOrWhiteSpace(settings.ProjectAbsoluteDir))
        {
            this.settings = new BuildAppSettings();
            slotManager = new AssemblySlotManager(FallbackSlotRoot(), logger);
        }
        else
        {
            this.settings = LoadSettings(settings);
            slotManager = new AssemblySlotManager(SlotRoot(this.settings), logger);
        }

        instance = instance is null ? this : throw new InvalidOperationException("BuildManager already initialized.");
    }

    // ── Settings ───────────────────────────────────────────────────────────────

    /// <summary>Registers MSBuild defaults.  Call once before the first compilation.</summary>
    public static void MsBuildLocatorRegisterDefaults() => MSBuildLocator.RegisterDefaults();

    /// <summary>Updates project settings (e.g. after the user opens a different project).</summary>
    public void UpdateSettings(IAppSettings newSettings)
    {
        ArgumentNullException.ThrowIfNull(newSettings);
        var updated = LoadSettings(newSettings);
        var currentRoot = string.IsNullOrWhiteSpace(settings.ProjectAbsoluteDir)
            ? FallbackSlotRoot()
            : SlotRoot(settings);
        var updatedRoot = SlotRoot(updated);

        if (!string.Equals(currentRoot, updatedRoot, StringComparison.OrdinalIgnoreCase))
        {
            slotManager.Unload();
            slotManager = new AssemblySlotManager(updatedRoot, logger);
            Serializer.ResetOptions();
        }

        settings = updated;
    }

    // ── Assembly management ────────────────────────────────────────────────────

    /// <summary>
    /// Returns only the most recently loaded user assembly.
    /// User code currently produces a single assembly.
    /// </summary>
    public Assembly? ActiveUserAssembly =>
        slotManager.LoadedAssemblies.LastOrDefault();

    /// <summary>All assemblies visible to the editor (entry + user assemblies).</summary>
    public IEnumerable<Assembly> LoadedAssemblies =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Concat(slotManager.LoadedAssemblies)
            .Distinct();

    /// <summary>Indicates whether a user assembly is currently loaded.</summary>
    public bool IsAssemblyLoaded => slotManager.LoadedAssemblyPath is not null;

    // ── Hot-reload / file watching ─────────────────────────────────────────────

    /// <summary>
    /// Starts watching <c>&lt;projectRoot&gt;/Assets</c> for <c>*.cs</c> changes.
    /// Each detected change (after debounce) enqueues a <see cref="CompileAndLoadAssemblyAsync"/> task
    /// when the Studio is active; otherwise the recompile is deferred until activation.
    /// </summary>
    public void EnableHotReload()
    {
        if (string.IsNullOrWhiteSpace(settings.ProjectAbsoluteDir))
        {
            logger.LogWarning("Cannot enable hot-reload: project not loaded");
            return;
        }

        sourceWatcher.SourceChanged -= OnSourceChanged;
        sourceWatcher.SourceChanged += OnSourceChanged;
        sourceWatcher.Start(settings.AssetsAbsoluteDir);
    }

    /// <summary>Stops the source file watcher.</summary>
    public void DisableHotReload()
    {
        sourceWatcher.SourceChanged -= OnSourceChanged;
        sourceWatcher.Stop();
    }

    void OnSourceChanged(string directory)
    {
        if (!studioIsActive)
        {
            pendingHotReload = true;
            logger.LogInformation(
                "Source change detected while Studio is inactive. Recompile deferred for {Directory}",
                directory);
            return;
        }

        if (isPlaying)
        {
            pendingHotReload = true;
            logger.LogInformation(
                "Source change detected during play mode. Recompile deferred for {Directory}. " +
                "Stop play to apply it",
                directory);
            return;
        }

        logger.LogInformation("Hot-reload triggered by change in {Directory}", directory);
        _ = CompileAndLoadAssemblyAsync();
    }

    /// <summary>
    /// Updates whether the Studio window is currently active.
    /// When transitioning back to active, any deferred hot-reload compilation is flushed.
    /// </summary>
    public void SetStudioActive(bool isActive)
    {
        var wasActive = studioIsActive;
        studioIsActive = isActive;

        if (isActive && !wasActive)
            FlushPendingHotReload("Studio reactivated");
    }

    /// <summary>
    /// Updates whether an in-editor play session is running. Recompiles are deferred while playing
    /// and flushed once play stops.
    /// </summary>
    /// <remarks>
    /// Swapping the user assembly mid-session would unload the <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
    /// that the live scene's components were created from, leaving dangling instances.
    /// </remarks>
    public void SetPlaying(bool playing)
    {
        var wasPlaying = isPlaying;
        isPlaying = playing;

        if (!playing && wasPlaying)
            FlushPendingHotReload("Play stopped");
    }

    void FlushPendingHotReload(string reason)
    {
        if (!pendingHotReload || !studioIsActive || isPlaying) return;

        pendingHotReload = false;
        logger.LogInformation("{Reason}. Flushing deferred hot-reload compilation", reason);
        _ = CompileAndLoadAssemblyAsync();
    }

    // ── Compile & Load ─────────────────────────────────────────────────────────

    /// <summary>
    /// Compiles the user project into the inactive slot and, on success, swaps it in.
    /// On failure the currently loaded assembly is preserved.
    /// This operation locks the UI while running.
    /// </summary>
    public Task<BuildTaskStatus> CompileAndLoadAssemblyAsync(bool recreateCsProj = false, bool forceRecompile = false)
    {
        // Retained for source compatibility; project generation is now always safe to repeat.
        _ = recreateCsProj;
        return TaskRunner.EnqueueAsync(new DelegateTask(
            name: "Compile & Load",
            locksUi: true,
            run: async _ =>
            {
                var assemblyPath = await CompileIntoInactiveSlot(forceRecompile).ConfigureAwait(false);

                if (!slotManager.TrySwapAndLoad(assemblyPath))
                    throw new InvalidOperationException("Assembly swap failed. Previous assembly retained.");

                return $"Loaded: {assemblyPath}";
            }));
    }

    // ── Play ───────────────────────────────────────────────────────────────────

    /// <summary>Indicates whether a play process is currently running.</summary>
    public bool IsPlaying
    {
        get { lock (playProcessLock) { return playProcess is { HasExited: false }; } }
    }

    /// <summary>
    /// Compiles the user code, then builds and launches the play runtime.
    /// </summary>
    public async Task PlayAsync(bool recreateCsProj = true)
    {
        // Retained for source compatibility; project generation is now always safe to repeat.
        _ = recreateCsProj;
        RequireProjectLoaded("play");

        var status = await TaskRunner.EnqueueAsync(new DelegateTask(
            name: "Play",
            locksUi: true,
            run: async _ =>
            {
                StopPlayProcessInternal(notify: false);

                // Compile user library first; pass the DLL path so PlayUserCode can cache the play build
                var userCodeDllPath = await CompileIntoInactiveSlot(forceRecompile: false).ConfigureAwait(false);

                var playUserCode = new PlayUserCode(settings, logger, userCodeDllPath);
                var launchResult = await playUserCode.ExecuteWithProcessAsync().ConfigureAwait(false);

                var proc = launchResult.Process;
                proc.EnableRaisingEvents = true;
                proc.Exited += OnPlayProcessExited;

                lock (playProcessLock) { playProcess = proc; }

                return $"Play started (PID {proc.Id}): {launchResult.RuntimePath}";
            })).ConfigureAwait(false);

        if (status.State == BuildTaskState.Succeeded)
            OnPlayStarted?.Invoke(status.Message);
        else
            OnPlayFailure?.Invoke(status.Message);
    }

    /// <summary>Stops the running play process.</summary>
    public void StopPlay() => StopPlayProcessInternal(notify: true);

    /// <summary>Stops the play process during shutdown without surfacing UI events.</summary>
    public void Shutdown() => StopPlayProcessInternal(notify: false);

    // ── Export ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Exports the project as a standalone packaged game.
    /// </summary>
    /// <param name="configuration">
    /// Override the build configuration.  Defaults to <see cref="BuildConfiguration.Release"/>.
    /// </param>
    public Task<BuildTaskStatus> ExportAsync(BuildConfiguration configuration = BuildConfiguration.Release) =>
        TaskRunner.EnqueueAsync(new DelegateTask(
            name: "Export",
            locksUi: true,
            run: async _ =>
            {
                var exporter = new ExportUserCode(settings, logger, configuration);
                var outputPath = await exporter.ExecuteAsync().ConfigureAwait(false);
                return $"Exported to: {outputPath}";
            }));

    // ── Dispose ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        DisableHotReload();
        sourceWatcher.Dispose();
        StopPlayProcessInternal(notify: false);
        TaskRunner.Dispose();
    }

    // ── Internal helpers ───────────────────────────────────────────────────────

    async Task<string> CompileIntoInactiveSlot(bool forceRecompile)
    {
        RequireProjectLoaded("compile");

        var compiler = new CompileUserCode(settings, logger, slotManager.InactiveSlotDirectory, forceRecompile);
        return await compiler.ExecuteAsync().ConfigureAwait(false);
    }

    void StopPlayProcessInternal(bool notify)
    {
        Process? target = null;

        lock (playProcessLock)
        {
            if (playProcess is { HasExited: false })
                target = playProcess;
            else
                playProcess = null;
        }

        if (target is null)
        {
            if (notify) OnPlayStopped?.Invoke("Play is not running.");
            return;
        }

        try
        {
            target.Exited -= OnPlayProcessExited;
            target.Kill(entireProcessTree: true);
            target.WaitForExit();

            lock (playProcessLock)
            {
                if (ReferenceEquals(playProcess, target))
                    playProcess = null;
            }

            logger.LogInformation("Play process stopped");
            if (notify) OnPlayStopped?.Invoke("Play stopped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop play process");
            OnPlayFailure?.Invoke("Failed to stop play: " + ex.Message);
        }
        finally
        {
            target.Dispose();
        }
    }

    void OnPlayProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process proc) return;

        proc.Exited -= OnPlayProcessExited;
        lock (playProcessLock)
        {
            if (ReferenceEquals(playProcess, proc))
                playProcess = null;
        }

        logger.LogInformation("Play process exited with code {Code}", proc.ExitCode);
        OnPlayStopped?.Invoke($"Play process exited with code {proc.ExitCode}.");
        proc.Dispose();
    }

    void RequireProjectLoaded(string operation)
    {
        if (string.IsNullOrWhiteSpace(settings.ProjectAbsoluteDir))
        {
            logger.LogError("Cannot {Operation}: project not loaded, open a project folder first", operation);
            throw new InvalidOperationException($"Cannot {operation}: project not loaded. Open a project folder first.");
        }
    }

    static BuildAppSettings LoadSettings(IAppSettings s) =>
        new BuildAppSettings().Load(s) as BuildAppSettings
        ?? throw new InvalidOperationException("Settings could not be loaded.");

    static string SlotRoot(BuildAppSettings s) =>
        Path.Combine(s.CacheAbsoluteDir, "bin");

    static string FallbackSlotRoot() =>
        Path.Combine(Path.GetTempPath(), "Turian", "bin");

    // ── Inner helper: anonymous task implementation ────────────────────────────

    sealed class DelegateTask(string name, bool locksUi, Func<CancellationToken, Task<string>> run)
        : IBuildTask
    {
        public string Name { get; } = name;
        public bool LocksUi { get; } = locksUi;
        public Task<string> RunAsync(CancellationToken ct) => run(ct);
    }
}
