namespace Gaya.Host;

/// <summary>
/// The editor's settings pages and the files behind them, one file per scope (Studio, User, Workspace, Personal).
/// A page allowing several scopes is resolved per member from the highest scope that holds it.
/// </summary>
/// <remarks>
/// Edits are saved as they settle rather than on a button: an edit marks its scope dirty and
/// <see cref="Flush"/> writes it once the user has stopped changing it, because dragging a number
/// field reports a change every frame and each one would otherwise be a file write.
/// </remarks>
public sealed class EditorSettings : IEditorSettings
{
    /// <summary>The project sub-directory a workspace's settings live in.</summary>
    public const string WorkspaceDirectory = SettingsPaths.WorkspaceDirectory;

    /// <summary>The file name the user, workspace and personal scopes use, in their own directory.</summary>
    public const string FileName = SettingsPaths.FileName;

    /// <summary>The file name of the Studio scope, in the per-user directory.</summary>
    public const string StudioFileName = SettingsPaths.StudioFileName;

    /// <summary>The per-user directory holding one sub-directory of personal values per project.</summary>
    public const string PersonalDirectory = SettingsPaths.PersonalDirectory;

    /// <summary>The file name of the Studio scope, in the per-user directory.</summary>
    public const string StudioFileName = "studio.json";

    /// <summary>The per-user directory holding one sub-directory of personal values per project.</summary>
    public const string PersonalDirectory = "projects";

    /// <summary>How long an edit may sit unwritten while the user keeps changing it.</summary>
    static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(1);

    /// <summary>How often <see cref="Flush"/> checks the files for changes made by another process.</summary>
    public TimeSpan ExternalChangeInterval { get; set; } = TimeSpan.FromSeconds(1);

    readonly ILogger log;
    readonly string personalRoot;
    readonly List<SettingsPageDescriptor> pages = [];
    readonly HashSet<SettingsScope> dirty = [];
    readonly Dictionary<string, JsonObject> baselines = new(StringComparer.Ordinal);
    readonly Stopwatch sinceWrite = Stopwatch.StartNew();
    readonly Stopwatch sinceCheck = Stopwatch.StartNew();

    readonly StudioSettingsStore studio;
    readonly StudioSettingsStore user;
    StudioSettingsStore? workspace;
    StudioSettingsStore? personal;

    /// <summary>Creates the settings over the user's file.</summary>
    /// <param name="log">Where a file that cannot be read or written is reported.</param>
    /// <param name="userPath">The user's settings file; the default location is used when null.</param>
    /// <param name="studioPath">The Studio scope's file; the default location is used when null.</param>
    /// <param name="personalRoot">The directory holding per-project personal files; the default location is used when null.</param>
    public EditorSettings(ILogger log, string? userPath = null, string? studioPath = null, string? personalRoot = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        this.log = log;
        this.personalRoot = personalRoot ?? UserConfigPath.For(PersonalDirectory);
        user = new StudioSettingsStore(log, userPath ?? SettingsPaths.User(UserConfigPath.Directory));
        studio = new StudioSettingsStore(log,
            studioPath ?? SettingsPaths.Studio(userPath is null ? UserConfigPath.Directory : Path.GetDirectoryName(userPath) ?? ""));
    }

    /// <inheritdoc />
    public IReadOnlyList<SettingsPageDescriptor> Pages =>
        [.. pages.OrderBy(static page => page.Order).ThenBy(static page => page.Path, StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc />
    public bool HasWorkspace => workspace is not null;

    /// <inheritdoc />
    public bool HasPersonal => personal is not null;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public string PathFor(SettingsScope scope) => Store(scope)?.Path ?? string.Empty;

    internal void RestoreUserPage(SettingsPageDescriptor page, string previousId) => user.Restore(page, previousId);

    /// <inheritdoc />
    public void Register(object target) => RegisterPage(new SettingsPageDescriptor(target));

    internal void RegisterPage(SettingsPageDescriptor page)
    {
        Resolve(page);

        var existing = pages.FindIndex(registered => registered.Id == page.Id);
        if (existing >= 0)
        {
            pages[existing] = page;
        }
        else
        {
            pages.Add(page);
        }

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Remove(string pageId)
    {
        if (pages.RemoveAll(page => page.Id == pageId) > 0)
        {
            baselines.Remove(pageId);
            Changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public void BindWorkspace(string? projectDirectory)
    {
        // Whatever the previous project's pages hold is written before it is let go, or an edit made
        // just before switching projects would be lost.
        WriteBeforeRebind(SettingsScope.Workspace);

        workspace = string.IsNullOrWhiteSpace(projectDirectory)
            ? null
            : new StudioSettingsStore(log, SettingsPaths.Workspace(projectDirectory));

        ResolveAll(SettingsScope.Workspace);

        log.LogDebug("Settings: workspace scope is {Path}", workspace?.Path ?? "unbound");
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void BindPersonal(string? projectId)
    {
        WriteBeforeRebind(SettingsScope.Personal);

        personal = SettingsPaths.Personal(projectId, personalRoot) is { } path ? new StudioSettingsStore(log, path) : null;

        ResolveAll(SettingsScope.Personal);

        log.LogDebug("Settings: personal scope is {Path}", personal?.Path ?? "unbound");
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
    public SettingsScope? EffectiveScope(string pageId, string member)
    {
        var held = ScopesHolding(pageId, member);
        return held.Count > 0 ? held[^1] : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<SettingsScope> ScopesHolding(string pageId, string member)
    {
        if (Page(pageId) is not { } page)
        {
            return [];
        }

        return [.. page.Layers.Where(scope => Store(scope)?.Has(page.Id, member) == true)];
    }

    /// <inheritdoc />
    public bool TryRead(string pageId, string member, SettingsScope scope, out object? value)
    {
        value = null;
        if (Page(pageId) is not { } page || !page.Allows(scope) || Store(scope) is not { } store
            || !store.Has(page.Id, member))
        {
            return false;
        }

        value = StudioSettingsStore.FromNode(page.Target.GetType(), member, store.Get(page.Id, member));
        return true;
    }

    /// <inheritdoc />
    public SettingsChange Write(string pageId, string member, SettingsScope scope, object? value)
    {
        if (Validate(pageId, member, scope) is { } failure)
        {
            return failure;
        }

        var page = Page(pageId)!;
        JsonNode? node;
        try
        {
            node = StudioSettingsStore.ToNode(page.Target.GetType(), member, value);
        }
        catch (ArgumentException ex)
        {
            return Failed(page, member, ex.Message);
        }

        return Commit(page, member, to: scope, node, clear: null);
    }

    /// <inheritdoc />
    public SettingsChange Copy(string pageId, string member, SettingsScope from, SettingsScope to) =>
        Transfer(pageId, member, from, to, move: false);

    /// <inheritdoc />
    public SettingsChange Move(string pageId, string member, SettingsScope from, SettingsScope to) =>
        Transfer(pageId, member, from, to, move: true);

    /// <inheritdoc />
    public SettingsChange Reset(string pageId, string member, SettingsScope scope)
    {
        if (Validate(pageId, member, scope) is { } failure)
        {
            return failure;
        }

        var page = Page(pageId)!;
        return Store(scope)!.Has(page.Id, member)
            ? Commit(page, member, to: null, node: null, clear: scope)
            : new SettingsChange(true, EffectiveScope(pageId, member));
    }

    SettingsChange Transfer(string pageId, string member, SettingsScope from, SettingsScope to, bool move)
    {
        if (from == to)
        {
            return Failed(Page(pageId), member, "The value is already in that scope.");
        }

        var invalid = Validate(pageId, member, from) ?? Validate(pageId, member, to);
        if (invalid is { } problem)
        {
            return problem;
        }

        var page = Page(pageId)!;
        if (Store(from)!.Get(page.Id, member) is not { } node)
        {
            return Failed(page, member, $"{from} holds no value for {member}.");
        }

        return Commit(page, member, to, node, move ? from : null);
    }

    /// <summary>
    /// Pending edits are written first so the resolve that follows cannot discard them. The destination
    /// is then written, and only once that has succeeded is the source cleared: whatever goes wrong
    /// leaves the value in the scope it was in or in both, and a value in both resolves to the same thing.
    /// </summary>
    SettingsChange Commit(SettingsPageDescriptor page, string member, SettingsScope? to, JsonNode? node, SettingsScope? clear)
    {
        Save();

        if (to is { } destination && !Store(destination)!.Update(page.Id,
                new Dictionary<string, JsonNode?> { [member] = node }, []))
        {
            return Failed(page, member, $"{destination} could not be written.");
        }

        var cleared = clear is not { } source || Store(source)!.Update(page.Id, new Dictionary<string, JsonNode?>(), [member]);
        Resolve(page);
        Changed?.Invoke();
        return cleared
            ? new SettingsChange(true, EffectiveScope(page.Id, member))
            : Failed(page, member, $"Copied to {to}, but {clear} could not be cleared.");
    }

    SettingsChange? Validate(string pageId, string member, SettingsScope scope)
    {
        if (Page(pageId) is not { } page)
        {
            return new SettingsChange(false, null, $"No settings page is registered as {pageId}.");
        }

        if (!page.Allows(scope))
        {
            return Failed(page, member, $"{page.Title} cannot be stored in the {scope} scope.");
        }

        return Store(scope) is null
            ? Failed(page, member, $"The {scope} scope is not available: {Unavailable(scope)}")
            : null;
    }

    SettingsChange Failed(SettingsPageDescriptor? page, string member, string error) =>
        new(false, page is null ? null : EffectiveScope(page.Id, member), error);

    static string Unavailable(SettingsScope scope) => scope switch
    {
        SettingsScope.Workspace => "no project is open.",
        SettingsScope.Personal => "the open project has no identity.",
        _ => "it is not bound.",
    };

    SettingsPageDescriptor? Page(string pageId) => pages.Find(registered => registered.Id == pageId);

    /// <inheritdoc />
    public void Save()
    {
        foreach (var scope in SettingsScopes.Ascending)
        {
            Write(scope);
        }

        WriteLayered();
        dirty.Clear();
    }

    /// <summary>
    /// Writes pending edits once they have settled. Called once a frame; it costs a comparison when
    /// nothing changed.
    /// </summary>
    public void Flush()
    {
        PickUpExternalChanges();
        if (dirty.Count == 0 || sinceWrite.Elapsed < WriteInterval)
        {
            return;
        }

        Save();
    }

    /// <summary>
    /// Re-reads a scope's file when another process, such as a CLI command, changed it, so a value written
    /// elsewhere applies without a restart. Pending edits are written first and so are never lost.
    /// </summary>
    void PickUpExternalChanges()
    {
        if (sinceCheck.Elapsed < ExternalChangeInterval)
        {
            return;
        }

        sinceCheck.Restart();
        var changed = SettingsScopes.Ascending.Where(scope => Store(scope)?.ChangedOnDisk() == true).ToList();
        if (changed.Count == 0)
        {
            return;
        }

        if (dirty.Count > 0)
        {
            Save();
        }

        foreach (var scope in changed)
        {
            Store(scope)!.Reload();
            ResolveAll(scope);
        }

        Changed?.Invoke();
    }

    void WriteBeforeRebind(SettingsScope scope)
    {
        if (Store(scope) is null)
        {
            return;
        }

        if (dirty.Contains(scope))
        {
            Write(scope);
        }

        WriteLayered();
    }

    /// <summary>Writes a scope's single-scope pages whole. A scope with no such page never gets a file of its own.</summary>
    void Write(SettingsScope scope)
    {
        dirty.Remove(scope);
        sinceWrite.Restart();

        if (Store(scope) is not { } store)
        {
            return;
        }

        var owned = pages.Where(page => page.Scope == scope && !page.IsLayered).ToList();
        if (owned.Count > 0)
        {
            store.Write(owned);
        }
    }

    /// <summary>
    /// Writes each edited member of the layered pages to the scope that holds it, or to the page's
    /// home scope when none does. A member that did not change is never written, so a scope stays
    /// as sparse as the user left it.
    /// </summary>
    void WriteLayered()
    {
        foreach (var page in pages.Where(page => page.IsLayered && page.Layers.Any(scope => Store(scope) is not null)))
        {
            if (Snapshot(page) is { } current && baselines.TryGetValue(page.Id, out var baseline))
            {
                Persist(page, current, baseline);
            }
        }
    }

    void Persist(SettingsPageDescriptor page, JsonObject current, JsonObject baseline)
    {
        foreach (var (layer, set) in Edits(page, current, baseline))
        {
            if (!Store(layer)!.Update(page.Id, set, []))
            {
                continue;
            }

            foreach (var name in set.Keys)
            {
                baseline[name] = current[name]?.DeepClone();
            }
        }
    }

    /// <summary>The members that differ from the baseline, grouped by the scope each one is written to.</summary>
    Dictionary<SettingsScope, Dictionary<string, JsonNode?>> Edits(SettingsPageDescriptor page, JsonObject current,
        JsonObject baseline)
    {
        var edits = new Dictionary<SettingsScope, Dictionary<string, JsonNode?>>();
        foreach (var (name, value) in current.Where(member => !JsonNode.DeepEquals(member.Value, baseline[member.Key])))
        {
            if (EditLayer(page, name) is not { } layer)
            {
                continue;
            }

            if (!edits.TryGetValue(layer, out var set))
            {
                edits[layer] = set = [];
            }

            set[name] = value;
        }

        return edits;
    }

    /// <summary>The scope an edit lands in: the highest that holds the member, else the home scope, else the highest bound.</summary>
    SettingsScope? EditLayer(SettingsPageDescriptor page, string member)
    {
        if (EffectiveScope(page.Id, member) is { } holder)
        {
            return holder;
        }

        if (Store(page.Scope) is not null)
        {
            return page.Scope;
        }

        return page.Layers.Where(scope => Store(scope) is not null).Select(static scope => (SettingsScope?)scope)
            .LastOrDefault();
    }

    JsonObject? Snapshot(SettingsPageDescriptor page) => user.Snapshot(page);

    void ResolveAll(SettingsScope scope)
    {
        foreach (var page in pages.Where(page => page.Allows(scope)))
        {
            Resolve(page);
        }
    }

    /// <summary>
    /// Brings a page's live object in line with its files. A single-scope page keeps what it has for
    /// members its file does not mention, as it always has. A layered page starts from the class's
    /// own defaults and applies each scope over the one below, so a member no scope holds any more
    /// falls back instead of keeping a stale value.
    /// </summary>
    void Resolve(SettingsPageDescriptor page)
    {
        if (!page.IsLayered)
        {
            Store(page.Scope)?.Restore(page);
            return;
        }

        SettingsDefaults.Restore(page.Target, page.Defaults);
        foreach (var scope in page.Layers)
        {
            Store(scope)?.Apply(page);
        }

        if (Snapshot(page) is { } snapshot)
        {
            baselines[page.Id] = snapshot;
        }
    }

    /// <summary>The file a scope is stored in. A scope that is not bound — no project, no identity — has none.</summary>
    StudioSettingsStore? Store(SettingsScope scope) => scope switch
    {
        SettingsScope.Studio => studio,
        SettingsScope.User => user,
        SettingsScope.Workspace => workspace,
        SettingsScope.Personal => personal,
        _ => null,
    };
}
