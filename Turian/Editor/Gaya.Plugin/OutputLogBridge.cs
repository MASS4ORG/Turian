namespace Gaya.Plugin.Turian;

/// <summary>
/// Clears Output when a play, build or recompile operation starts, preserving that operation's diagnostics.
/// The panel reads immutable log snapshots and resets its selection on the next UI tick.
/// </summary>
public sealed class OutputLogBridge
{
    readonly OutputPanelSettings settings;
    long pendingClears;
    long generation;

    /// <summary>Creates the bridge and starts following the editor's play and build lifecycle.</summary>
    /// <param name="settings">The Output console's settings, read while ticking.</param>
    /// <param name="playMode">The play session whose start may clear the console.</param>
    /// <param name="build">The build manager whose tasks and assembly swaps may clear the console.</param>
    public OutputLogBridge(OutputPanelSettings settings, PlayModeService playMode, BuildManager build)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(playMode);
        ArgumentNullException.ThrowIfNull(build);

        this.settings = settings;
        playMode.Starting += OnPlayStarting;
        build.TaskRunner.TaskStarted += OnBuildStarting;
    }

    /// <summary>
    /// How many clears were applied. The panel compares it across frames to re-anchor its list when
    /// the console emptied beneath it.
    /// </summary>
    public long Generation => Interlocked.Read(ref generation);

    /// <summary>
    /// Publishes operation-start clears to the panel so its selection resets on the UI thread.
    /// </summary>
    public void Tick()
    {
        Interlocked.Add(ref generation, Interlocked.Exchange(ref pendingClears, 0));
    }

    void OnPlayStarting()
    {
        if (settings.ClearOnPlay)
        {
            Apply();
        }
    }

    void OnBuildStarting(string taskName)
    {
        if (settings.ClearOnRecompile && taskName == "Compile & Load"
            || settings.ClearOnBuild && taskName is "Play" or "Export")
        {
            Apply();
        }
    }

    void Apply()
    {
        LogBuffer.Clear();
        Interlocked.Increment(ref pendingClears);
    }
}
