namespace Turian.Editor.Core;

/// <summary>
/// Discovers the settings pages user code and editor bricks contribute with
/// <see cref="Gaya.EditorSettingAttribute"/>: an annotated class with a public parameterless constructor becomes a page
/// in the editor's settings. Framework-agnostic — it yields paths and instances, and a shell turns those into whatever
/// its settings panel is made of.
/// </summary>
/// <remarks>
/// A page keeps its instance while its assembly stays in force, and a recompile creates fresh ones. The values the
/// user had set are restored from the settings file when a page is registered again.
/// </remarks>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class UserSettingsCatalog
{
    readonly BuildManager buildManager;
    readonly EditorAssemblySet assemblies;
    readonly ILogger log;

    readonly List<UserSettingsPage> pages = [];
    readonly Dictionary<ReflectionAssembly, IReadOnlyList<UserSettingsPage>> scanned = [];
    IReadOnlyList<ReflectionAssembly>? scannedUsers;
    IReadOnlyList<ReflectionAssembly>? scannedEditors;

    /// <summary>Creates the catalog over the build manager that owns the user assembly.</summary>
    /// <param name="buildManager">Supplies the currently loaded user assemblies.</param>
    /// <param name="log">Where a page that cannot be created is reported.</param>
    /// <param name="assemblies">The editor assemblies; one over <paramref name="buildManager"/> when omitted.</param>
    public UserSettingsCatalog(BuildManager buildManager, ILogger log, EditorAssemblySet? assemblies = null)
    {
        ArgumentNullException.ThrowIfNull(buildManager);
        ArgumentNullException.ThrowIfNull(log);

        this.buildManager = buildManager;
        this.assemblies = assemblies ?? new EditorAssemblySet(buildManager);
        this.log = log;
    }

    /// <summary>Raised after a rescan produced a different set of pages, so a shell can re-register them.</summary>
    public event Action? Changed;

    /// <summary>
    /// The pages currently contributed. Rescans when an assembly joined or left since the last call, which is what
    /// makes the settings follow a recompile or a brick change.
    /// </summary>
    public IReadOnlyList<UserSettingsPage> Pages
    {
        get
        {
            EnsureRefreshed();
            return pages;
        }
    }

    /// <summary>Rescans if the contributing assemblies changed. Cheap when they have not.</summary>
    public void EnsureRefreshed()
    {
        var users = buildManager.ActiveUserAssembly is null ? [] : buildManager.ActiveUserAssemblies;
        var editors = assemblies.Current;
        if (ReferenceEquals(users, scannedUsers) && ReferenceEquals(editors, scannedEditors))
        {
            return;
        }

        scannedUsers = users;
        scannedEditors = editors;
        Rescan([.. users.Concat(editors).Distinct()]);
    }

    void Rescan(IReadOnlyList<ReflectionAssembly> current)
    {
        foreach (var gone in scanned.Keys.Except(current).ToList())
        {
            scanned.Remove(gone);
        }

        var next = new List<UserSettingsPage>();
        foreach (var assembly in current)
        {
            if (!scanned.TryGetValue(assembly, out var found))
            {
                scanned.Add(assembly, found = Scan(assembly, log));
            }

            next.AddRange(found);
        }

        if (next.SequenceEqual(pages))
        {
            return;
        }

        pages.Clear();
        pages.AddRange(next);
        log.LogDebug("User settings: {Count} page(s) from {Assemblies} assemblies", pages.Count, current.Count);
        Changed?.Invoke();
    }

    /// <summary>
    /// The pages an assembly contributes, without any editor state behind it. Separate from the
    /// catalog so the discovery rules can be exercised against any assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="log">Where a skipped type is reported.</param>
    /// <returns>One page per usable annotated class, in reflection order.</returns>
    public static IReadOnlyList<UserSettingsPage> Scan(ReflectionAssembly assembly, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(log);

        var found = new List<UserSettingsPage>();

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

            found.Add(new UserSettingsPage(attribute.IdFor(type), attribute.Path.Trim(),
                target, attribute.Description, attribute.Order, attribute.Workspace));
        }

        return found;
    }

    /// <summary>
    /// Every annotated class in the assembly. A type that fails to load — one referencing a package
    /// that is no longer restored — is skipped rather than aborting the scan, matching how
    /// <c>TypeRegistry</c> treats the same failure.
    /// </summary>
    static IEnumerable<(Type Type, Gaya.EditorSettingAttribute Attribute)> AnnotatedTypes(
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
            log.LogWarning(ex, "User settings: could not read the types in {Assembly}", assembly.GetName().Name);
            yield break;
        }

        foreach (var type in types.Where(static type => type is { IsClass: true, IsAbstract: false }))
        {
            Gaya.EditorSettingAttribute? attribute = null;
            try
            {
                attribute = type.GetCustomAttribute<Gaya.EditorSettingAttribute>(inherit: false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "User settings: could not read the attributes on {Type}", type.FullName);
            }

            if (attribute is not null)
            {
                yield return (type, attribute);
            }
        }
    }

    /// <summary>
    /// Instantiates a page, rejecting a type the editor cannot construct. One bad page must not cost
    /// the project the rest of its settings.
    /// </summary>
    static object? Create(Type type, ILogger log)
    {
        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            log.LogWarning("User settings: {Type} needs a public parameterless constructor", type.FullName);
            return null;
        }

        try
        {
            return Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "User settings: {Type} could not be created", type.FullName);
            return null;
        }
    }
}
