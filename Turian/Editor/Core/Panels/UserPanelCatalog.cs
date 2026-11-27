namespace Turian.Editor.Core;

/// <summary>
/// Discovers the panels user code contributes with <see cref="PanelAttribute"/>: an annotated class
/// with a public parameterless constructor becomes a dockable panel, drawn the way the Settings panel
/// draws a settings page — its public members reflected into a form, its <c>[Button]</c> methods drawn
/// as actions. Framework-agnostic — it yields plain objects, and a shell turns those into whatever its
/// dock space is made of.
/// </summary>
/// <remarks>
/// Rescanning creates fresh instances, because a recompile replaces the types they came from. A panel
/// has no persisted values to restore, unlike a settings page — it is a workbench for the session, not
/// a preference.
/// </remarks>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class UserPanelCatalog : IDisposable
{
    readonly EditorAssemblySet assemblies;
    readonly ILogger log;

    readonly List<UserPanelPage> pages = [];
    readonly Dictionary<ReflectionAssembly, IReadOnlyList<UserPanelPage>> scanned = [];
    bool disposed;

    /// <summary>Creates the catalog over the build manager that owns the user assembly.</summary>
    /// <param name="buildManager">Supplies the currently loaded user assembly.</param>
    /// <param name="log">Where a page that cannot be created is reported.</param>
    /// <param name="assemblies">The assemblies to scan; one over <paramref name="buildManager"/> when omitted.</param>
    public UserPanelCatalog(BuildManager buildManager, ILogger log, EditorAssemblySet? assemblies = null)
    {
        ArgumentNullException.ThrowIfNull(buildManager);
        ArgumentNullException.ThrowIfNull(log);

        this.assemblies = assemblies ?? new EditorAssemblySet(buildManager);
        this.log = log;
    }

    /// <summary>Raised after a rescan produced a different set of panels, so a shell can re-register them.</summary>
    public event Action? Changed;

    /// <summary>Raised when a document editor panel should be shown.</summary>
    public event Action<string>? OpenRequested;

    internal AssetActivation? OpenDocument(string sourcePath, bool isReadOnly = false)
    {
        foreach (var page in Pages)
        {
            if (page.Target is IAssetDocumentEditor editor && Accepts(page, editor, sourcePath))
            {
                return OpenDocument(page, editor, sourcePath, isReadOnly);
            }
        }

        return null;
    }

    bool Accepts(UserPanelPage page, IAssetDocumentEditor editor, string sourcePath)
    {
        try
        {
            return editor.CanOpen(sourcePath);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Document editor {Panel} could not check {Path}", page.Id, sourcePath);
            return false;
        }
    }

    AssetActivation OpenDocument(UserPanelPage page, IAssetDocumentEditor editor, string sourcePath, bool isReadOnly)
    {
        try
        {
            editor.Open(sourcePath, isReadOnly);
            OpenRequested?.Invoke(page.Id);
            return AssetActivation.Edit;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Document editor {Panel} could not open {Path}", page.Id, sourcePath);
            return AssetActivation.None;
        }
    }

    /// <summary>
    /// The panels user code currently contributes. Rescans when the user assembly has been swapped
    /// since the last call, which is what makes the panel list follow a recompile.
    /// </summary>
    public IReadOnlyList<UserPanelPage> Pages
    {
        get
        {
            EnsureRefreshed();
            return pages;
        }
    }

    /// <summary>Refreshes panels when their contributing assemblies change.</summary>
    public void EnsureRefreshed()
    {
        if (disposed)
        {
            return;
        }

        Refresh(assemblies.Current);
    }

    internal IReadOnlyList<UserPanelPage> Refresh(IReadOnlyCollection<ReflectionAssembly> current)
    {
        var removed = RemoveAssemblies(current, out var changed);
        changed |= AddAssemblies(current);

        if (!changed)
        {
            return pages;
        }

        var previous = pages.Count;
        pages.Clear();
        pages.AddRange(scanned.Values.SelectMany(static value => value));
        if (previous != 0 || pages.Count != 0)
        {
            Changed?.Invoke();
        }

        DisposePages(removed);
        return pages;
    }

    List<UserPanelPage> RemoveAssemblies(IReadOnlyCollection<ReflectionAssembly> current, out bool changed)
    {
        var gone = scanned.Keys.Except(current).ToArray();
        changed = gone.Length > 0;
        var removed = new List<UserPanelPage>();
        foreach (var assembly in gone)
        {
            removed.AddRange(scanned[assembly]);
            scanned.Remove(assembly);
        }

        return removed;
    }

    bool AddAssemblies(IReadOnlyCollection<ReflectionAssembly> current)
    {
        var changed = false;
        foreach (var assembly in current)
        {
            if (!scanned.ContainsKey(assembly))
            {
                scanned.Add(assembly, Scan(assembly, log));
                changed = true;
            }
        }

        return changed;
    }

    void DisposePages(IEnumerable<UserPanelPage> removed)
    {
        foreach (var page in removed)
        {
            if (page.Target is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "User panel {Panel} could not be disposed", page.Id);
                }
            }
        }
    }

    /// <summary>Releases the catalog's panel instances.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Refresh([]);
    }

    /// <summary>
    /// The panels an assembly contributes, without any editor state behind it. Separate from the
    /// catalog so the discovery rules can be exercised against any assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="log">Where a skipped type is reported.</param>
    /// <returns>One page per usable annotated class, in reflection order.</returns>
    public static IReadOnlyList<UserPanelPage> Scan(ReflectionAssembly assembly, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(log);

        var found = new List<UserPanelPage>();

        foreach (var (type, attribute) in AnnotatedTypes(assembly, log))
        {
            if (string.IsNullOrWhiteSpace(attribute.Path))
            {
                continue;
            }

            if (Create(type, log) is not { } target)
            {
                continue;
            }

            found.Add(new UserPanelPage($"usercode.panel.{type.FullName}", attribute.Name,
                attribute.Path.Trim(), target));
        }

        return found;
    }

    /// <summary>
    /// Every annotated class in the assembly. A type that fails to load — one referencing a package
    /// that is no longer restored — is skipped rather than aborting the scan, matching how
    /// <c>TypeRegistry</c> treats the same failure.
    /// </summary>
    static IEnumerable<(Type Type, PanelAttribute Attribute)> AnnotatedTypes(
        ReflectionAssembly assembly, ILogger log)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.Where(static type => type is not null)!];
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "User panels: could not read the types in {Assembly}", assembly.GetName().Name);
            yield break;
        }

        foreach (var type in types.Where(static type => type is { IsClass: true, IsAbstract: false }))
        {
            PanelAttribute? attribute = null;
            try
            {
                attribute = type.GetCustomAttribute<PanelAttribute>(inherit: false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "User panels: could not read the attributes on {Type}", type.FullName);
            }

            if (attribute is not null)
            {
                yield return (type, attribute);
            }
        }
    }

    /// <summary>
    /// Instantiates a panel, rejecting a type the editor cannot construct. One bad panel must not cost
    /// the project the rest of them.
    /// </summary>
    static object? Create(Type type, ILogger log)
    {
        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            log.LogWarning("User panels: {Type} needs a public parameterless constructor", type.FullName);
            return null;
        }

        try
        {
            return Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "User panels: {Type} could not be created", type.FullName);
            return null;
        }
    }
}
