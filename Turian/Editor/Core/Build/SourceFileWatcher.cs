namespace Turian.Editor.Core;

/// <summary>
/// Watches a project directory for changes to <c>*.cs</c> files and assembly definitions, and raises
/// <see cref="SourceChanged"/> after a configurable debounce period so that
/// rapid saves do not trigger many back-to-back recompiles.
///
/// <para>
/// Extends <see cref="ProjectDirectoryWatcher"/> which manages the underlying
/// <see cref="System.IO.FileSystemWatcher"/> lifecycle (Start / Stop / Dispose).
/// </para>
/// </summary>
public sealed class SourceFileWatcher : ProjectDirectoryWatcher
{
    readonly TimeSpan debounce;
    Timer? debounceTimer;
    readonly object timerLock = new();

    // ── Public surface ─────────────────────────────────────────────────────────

    /// <summary>
    /// Raised (on a thread-pool thread) after sources change and the debounce
    /// period elapses.  The argument is the directory being watched.
    /// </summary>
    public event Action<string>? SourceChanged;

    // ── Watcher configuration ──────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Every file is watched; <see cref="IsSource"/> keeps scripts and assembly definitions.</remarks>
    protected override string Filter => "*.*";

    /// <inheritdoc/>
    protected override NotifyFilters WatcherNotifyFilters =>
        NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;

    // ── Constructor ────────────────────────────────────────────────────────────

    /// <param name="logger">The logger.</param>
    /// <param name="debounce">
    /// How long to wait after the last change before firing <see cref="SourceChanged"/>.
    /// Defaults to 500 ms.
    /// </param>
    public SourceFileWatcher(ILogger logger, TimeSpan debounce = default) : base(logger)
    {
        this.debounce = debounce == default ? TimeSpan.FromMilliseconds(500) : debounce;
    }

    // ── File-event hooks ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void OnFileCreated(FileSystemEventArgs e)
    {
        if (!IsSource(e.FullPath)) return;
        Logger.LogDebug("Source change detected: {ChangeType} {Path}", e.ChangeType, e.FullPath);
        ResetDebounce();
    }

    /// <inheritdoc/>
    protected override void OnFileChanged(FileSystemEventArgs e)
    {
        if (!IsSource(e.FullPath)) return;
        Logger.LogDebug("Source change detected: {ChangeType} {Path}", e.ChangeType, e.FullPath);
        ResetDebounce();
    }

    /// <inheritdoc/>
    protected override void OnFileDeleted(FileSystemEventArgs e)
    {
        if (!IsSource(e.FullPath)) return;
        Logger.LogDebug("Source change detected: {ChangeType} {Path}", e.ChangeType, e.FullPath);
        ResetDebounce();
    }

    /// <inheritdoc/>
    protected override void OnFileRenamed(RenamedEventArgs e)
    {
        if (!IsSource(e.FullPath) && !IsSource(e.OldFullPath)) return;
        Logger.LogDebug("Source change detected: {ChangeType} {Path}", e.ChangeType, e.FullPath);
        ResetDebounce();
    }

    /// <summary>
    /// Whether a change to <paramref name="path"/> can change what compiles: a script, or a data asset holding an
    /// assembly definition (any removed data asset counts, since its content can no longer be read).
    /// </summary>
    static bool IsSource(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || (GenericAssetImporter.IsDataAssetPath(path) && (!File.Exists(path) || AssemblyGraph.IsDefinitionFile(path)));

    // ── Disposal ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Also disposes the internal debounce timer.</remarks>
    protected override void DisposeResources()
    {
        lock (timerLock)
        {
            debounceTimer?.Dispose();
            debounceTimer = null;
        }
    }

    // ── Debounce ───────────────────────────────────────────────────────────────

    void ResetDebounce()
    {
        lock (timerLock)
        {
            if (debounceTimer is null)
                debounceTimer = new Timer(FireSourceChanged, null, debounce, Timeout.InfiniteTimeSpan);
            else
                debounceTimer.Change(debounce, Timeout.InfiniteTimeSpan);
        }
    }

    void FireSourceChanged(object? _)
    {
        lock (timerLock)
        {
            debounceTimer?.Dispose();
            debounceTimer = null;
        }

        var directory = WatchedDirectory ?? string.Empty;
        Logger.LogInformation("Source changed – triggering recompile for {Directory}", directory);
        SourceChanged?.Invoke(directory);
    }
}
