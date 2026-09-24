namespace Turian.Editor.Core;

/// <summary>
/// Abstract base class for monitoring a project directory with a <see cref="System.IO.FileSystemWatcher"/>.
///
/// <para>
/// Manages the watcher lifecycle (<see cref="Start"/>, <see cref="Stop"/>, <see cref="IDisposable.Dispose"/>)
/// and exposes protected virtual hooks (<see cref="OnFileCreated"/>, <see cref="OnFileChanged"/>,
/// <see cref="OnFileDeleted"/>, <see cref="OnFileRenamed"/>) for subclasses to handle file-system events.
/// </para>
///
/// <para>
/// Subclasses can customise the underlying watcher behavior by overriding
/// <see cref="Filter"/>, <see cref="IncludeSubdirectories"/>, and <see cref="WatcherNotifyFilters"/>.
/// Additional cleanup on disposal is supported via <see cref="DisposeResources"/>.
/// </para>
/// </summary>
public abstract class ProjectDirectoryWatcher : IDisposable
{
    FileSystemWatcher? watcher;
    bool disposed;

    // ── Protected surface ──────────────────────────────────────────────────────

    /// <summary>Structured logger available to subclasses.</summary>
    protected readonly ILogger Logger;

    /// <summary>
    /// File-system filter string forwarded to the underlying <see cref="FileSystemWatcher"/>.
    /// Defaults to <c>"*.*"</c> (all files). Override to narrow the watch scope (e.g. <c>"*.cs"</c>).
    /// </summary>
    protected virtual string Filter => "*.*";

    /// <summary>
    /// Whether sub-directories are included in the watch. Defaults to <see langword="true"/>.
    /// </summary>
    protected virtual bool IncludeSubdirectories => true;

    /// <summary>
    /// <see cref="NotifyFilters"/> forwarded to the underlying watcher.
    /// Defaults to <see cref="NotifyFilters.FileName"/> |
    /// <see cref="NotifyFilters.DirectoryName"/> | <see cref="NotifyFilters.LastWrite"/>.
    /// </summary>
    protected virtual NotifyFilters WatcherNotifyFilters =>
        NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;

    // ── Public state ───────────────────────────────────────────────────────────

    /// <summary>The directory currently being watched, or <see langword="null"/> if the watcher is inactive.</summary>
    public string? WatchedDirectory => watcher?.Path;

    /// <summary>Indicates whether the watcher is currently active and raising events.</summary>
    public bool IsWatching => watcher is { EnableRaisingEvents: true };

    // ── Constructor ────────────────────────────────────────────────────────────

    /// <param name="logger">The logger used by this instance and available to subclasses via <see cref="Logger"/>.</param>
    protected ProjectDirectoryWatcher(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Logger = logger;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts watching <paramref name="directory"/> using the current <see cref="Filter"/> and
    /// <see cref="WatcherNotifyFilters"/>. Any previously active watch is stopped first.
    /// </summary>
    /// <param name="directory">Absolute path to the directory to watch.</param>
    /// <exception cref="ArgumentException">When <paramref name="directory"/> is null or whitespace.</exception>
    /// <exception cref="DirectoryNotFoundException">When <paramref name="directory"/> does not exist.</exception>
    public void Start(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Stop();

        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");

        watcher = new FileSystemWatcher(fullPath, Filter)
        {
            IncludeSubdirectories = IncludeSubdirectories,
            NotifyFilter = WatcherNotifyFilters,
            EnableRaisingEvents = true
        };

        watcher.Created += OnCreatedCore;
        watcher.Changed += OnChangedCore;
        watcher.Deleted += OnDeletedCore;
        watcher.Renamed += OnRenamedCore;

        Logger.LogInformation("{WatcherType} started: {Directory}", GetType().Name, fullPath);
        OnStarted(fullPath);
    }

    /// <summary>Stops the active watcher, detaches all event handlers, and releases the underlying resource.</summary>
    public void Stop()
    {
        if (watcher is null) return;

        watcher.EnableRaisingEvents = false;
        watcher.Created -= OnCreatedCore;
        watcher.Changed -= OnChangedCore;
        watcher.Deleted -= OnDeletedCore;
        watcher.Renamed -= OnRenamedCore;
        watcher.Dispose();
        watcher = null;

        Logger.LogInformation("{WatcherType} stopped", GetType().Name);
        OnStopped();
    }

    // ── Extensibility hooks ────────────────────────────────────────────────────

    /// <summary>
    /// Called immediately after the watcher has been successfully started.
    /// Override to perform any post-start initialisation.
    /// </summary>
    /// <param name="directory">The fully-qualified directory path that is now being watched.</param>
    protected virtual void OnStarted(string directory) { }

    /// <summary>
    /// Called immediately after the watcher has been stopped.
    /// Override to perform any post-stop cleanup.
    /// </summary>
    protected virtual void OnStopped() { }

    /// <summary>Invoked when a file or directory is created inside the watched path.</summary>
    protected virtual void OnFileCreated(FileSystemEventArgs e) { }

    /// <summary>Invoked when a file or directory is modified inside the watched path.</summary>
    protected virtual void OnFileChanged(FileSystemEventArgs e) { }

    /// <summary>Invoked when a file or directory is deleted inside the watched path.</summary>
    protected virtual void OnFileDeleted(FileSystemEventArgs e) { }

    /// <summary>Invoked when a file or directory is renamed inside the watched path.</summary>
    protected virtual void OnFileRenamed(RenamedEventArgs e) { }

    /// <summary>
    /// Called during <see cref="Dispose"/> after the watcher has been stopped.
    /// Override to release any additional resources owned by a subclass
    /// (e.g. debounce timers).
    /// </summary>
    protected virtual void DisposeResources() { }

    // ── IDisposable ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Stop();
        DisposeResources();

        GC.SuppressFinalize(this);
    }

    // ── Private routing ────────────────────────────────────────────────────────

    void OnCreatedCore(object _, FileSystemEventArgs e) => OnFileCreated(e);
    void OnChangedCore(object _, FileSystemEventArgs e) => OnFileChanged(e);
    void OnDeletedCore(object _, FileSystemEventArgs e) => OnFileDeleted(e);
    void OnRenamedCore(object _, RenamedEventArgs e) => OnFileRenamed(e);
}
