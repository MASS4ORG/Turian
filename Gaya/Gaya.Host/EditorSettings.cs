namespace Gaya.Host;

/// <summary>
/// The editor's settings pages and the two files behind them: <c>~/.gaya/settings.json</c> for
/// the user's own, and <c>&lt;project&gt;/.gaya/settings.json</c> for the ones that belong to
/// the open project.
/// </summary>
/// <remarks>
/// Edits are saved as they settle rather than on a button: an edit marks its scope dirty and
/// <see cref="Flush"/> writes it once the user has stopped changing it, because dragging a number
/// field reports a change every frame and each one would otherwise be a file write.
/// </remarks>
public sealed class EditorSettings : IEditorSettings
{
    /// <summary>The project sub-directory a workspace's settings live in.</summary>
    public const string WorkspaceDirectory = ".gaya";

    /// <summary>The file name both scopes use, in their own directory.</summary>
    public const string FileName = "settings.json";

    /// <summary>How long an edit may sit unwritten while the user keeps changing it.</summary>
    static readonly TimeSpan writeInterval = TimeSpan.FromSeconds(1);

    readonly ILogger log;
    readonly List<SettingsPageDescriptor> pages = [];
    readonly HashSet<SettingsScope> dirty = [];
    readonly Stopwatch sinceWrite = Stopwatch.StartNew();

    readonly StudioSettingsStore user;
    StudioSettingsStore? workspace;

    /// <summary>Creates the settings over the user's file.</summary>
    /// <param name="log">Where a file that cannot be read or written is reported.</param>
    /// <param name="userPath">The user's settings file; the default location is used when null.</param>
    public EditorSettings(ILogger log, string? userPath = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        this.log = log;
        user = new StudioSettingsStore(log, userPath ?? UserConfigPath.For(FileName));
    }

    /// <inheritdoc />
    public IReadOnlyList<SettingsPageDescriptor> Pages =>
        [.. pages.OrderBy(page => page.Order).ThenBy(page => page.Path, StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc />
    public bool HasWorkspace => workspace is not null;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public string PathFor(SettingsScope scope) =>
        scope == SettingsScope.User ? user.Path : workspace?.Path ?? string.Empty;

    /// <inheritdoc />
    public void Register(SettingsPageDescriptor page)
    {
        ArgumentNullException.ThrowIfNull(page);

        Store(page.Scope)?.Restore(page);

        var existing = pages.FindIndex(registered => registered.Id == page.Id);
        if (existing >= 0) pages[existing] = page;
        else pages.Add(page);

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Remove(string pageId)
    {
        if (pages.RemoveAll(page => page.Id == pageId) > 0) Changed?.Invoke();
    }

    /// <inheritdoc />
    public void BindWorkspace(string? projectDirectory)
    {
        // Whatever the previous project's pages hold is written before it is let go, or an edit made
        // just before switching projects would be lost.
        if (workspace is not null && dirty.Contains(SettingsScope.Workspace)) Write(SettingsScope.Workspace);

        workspace = string.IsNullOrWhiteSpace(projectDirectory)
            ? null
            : new StudioSettingsStore(log,
                Path.Combine(projectDirectory, WorkspaceDirectory, FileName));

        foreach (var page in pages.Where(page => page.Scope == SettingsScope.Workspace))
            workspace?.Restore(page);

        log.LogDebug("Settings: workspace scope is {Path}", workspace?.Path ?? "unbound");
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void NotifyChanged(string pageId)
    {
        var page = pages.Find(registered => registered.Id == pageId);
        dirty.Add(page?.Scope ?? SettingsScope.User);
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Save()
    {
        Write(SettingsScope.User);
        if (workspace is not null) Write(SettingsScope.Workspace);
    }

    /// <summary>
    /// Writes pending edits once they have settled. Called once a frame; it costs a comparison when
    /// nothing changed.
    /// </summary>
    public void Flush()
    {
        if (dirty.Count == 0 || sinceWrite.Elapsed < writeInterval) return;

        Save();
    }

    void Write(SettingsScope scope)
    {
        dirty.Remove(scope);
        sinceWrite.Restart();

        Store(scope)?.Write(pages.Where(page => page.Scope == scope));
    }

    /// <summary>
    /// The file a scope is stored in. A workspace page with no project open has none: it keeps its
    /// values in memory until a project is opened, rather than leaking into the user's file.
    /// </summary>
    StudioSettingsStore? Store(SettingsScope scope) =>
        scope == SettingsScope.User ? user : workspace;
}
