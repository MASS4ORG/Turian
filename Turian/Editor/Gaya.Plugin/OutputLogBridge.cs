namespace Gaya.Plugin.Turian;

/// <summary>
/// Watches the editor for the moments the Output console's "clear on …" toggles listen for — entering
/// play, a completed standalone build (Build &amp; Run or Export), or a fresh user assembly from a
/// recompile — and empties the log buffer when the matching toggle is on. <see cref="Tick"/> runs on
/// the UI thread once a frame, so events that arrive from a background task's completion only mark a
/// pending clear there, and <see cref="Tick"/> applies it where it cannot race the panel's own render.
/// </summary>
public sealed class OutputLogBridge
{
    readonly OutputPanelSettings settings;
    readonly ILogger log;
    readonly BuildManager build;

    Assembly? lastAssembly;
    volatile bool pendingPlay;
    volatile bool pendingBuild;
    long generation;

    /// <summary>Creates the bridge and starts following the editor's play and build lifecycle.</summary>
    /// <param name="settings">The Output console's settings, read while ticking.</param>
    /// <param name="playMode">The play session whose start may clear the console.</param>
    /// <param name="build">The build manager whose tasks and assembly swaps may clear the console.</param>
    /// <param name="log">Where each applied clear is reported.</param>
    public OutputLogBridge(OutputPanelSettings settings, PlayModeService playMode, BuildManager build,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(playMode);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(log);

        this.settings = settings;
        this.build = build;
        this.log = log;

        lastAssembly = build.ActiveUserAssembly;

        playMode.StateChanged += OnPlayState;
        build.TaskRunner.TaskCompleted += OnBuildTask;
    }

    /// <summary>
    /// How many clears were applied. The panel compares it across frames to re-anchor its list when
    /// the console emptied beneath it.
    /// </summary>
    public long Generation => Interlocked.Read(ref generation);

    /// <summary>
    /// Applies whatever clear is pending. Called once a frame from the plugin's tick — the same
    /// thread that renders the panel, so the buffer cannot change mid-frame.
    /// </summary>
    public void Tick()
    {
        if (settings.ClearOnPlay && pendingPlay)
        {
            pendingPlay = false;
            Apply("Play");
        }

        if (settings.ClearOnBuild && pendingBuild)
        {
            pendingBuild = false;
            Apply("Build");
        }

        if (settings.ClearOnRecompile) CheckRecompile();
    }

    void CheckRecompile()
    {
        var current = build.ActiveUserAssembly;
        if (ReferenceEquals(current, lastAssembly)) return;

        lastAssembly = current;
        Apply("Recompile");
    }

    void OnPlayState(PlayState state)
    {
        if (state == PlayState.Playing) pendingPlay = true;
    }

    void OnBuildTask(BuildTaskStatus status)
    {
        if (status.State == BuildTaskState.Succeeded && status.TaskName is "Play" or "Export")
            pendingBuild = true;
    }

    void Apply(string reason)
    {
        LogBuffer.Clear();
        Interlocked.Increment(ref generation);
        log.LogDebug("Output: cleared on {Reason}", reason);
    }
}
