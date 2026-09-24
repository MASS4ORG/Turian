namespace Turian.Editor.Core;

/// <summary>
/// Discovers the settings pages user code contributes with <see cref="EditorSettingAttribute"/>: an
/// annotated class with a public parameterless constructor becomes a page in the editor's settings.
/// Framework-agnostic — it yields paths and instances, and a shell turns those into whatever its
/// settings panel is made of.
/// </summary>
/// <remarks>
/// Rescanning creates fresh instances, because a recompile replaces the types they came from. The
/// values the user had set are restored from the settings file when the page is registered again.
/// </remarks>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class UserSettingsCatalog
{
    readonly BuildManager buildManager;
    readonly ILogger log;

    readonly List<UserSettingsPage> pages = [];
    Assembly? scanned;
    bool scannedOnce;

    /// <summary>Creates the catalog over the build manager that owns the user assembly.</summary>
    /// <param name="buildManager">Supplies the currently loaded user assembly.</param>
    /// <param name="log">Where a page that cannot be created is reported.</param>
    public UserSettingsCatalog(BuildManager buildManager, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(buildManager);
        ArgumentNullException.ThrowIfNull(log);

        this.buildManager = buildManager;
        this.log = log;
    }

    /// <summary>Raised after a rescan produced a different set of pages, so a shell can re-register them.</summary>
    public event Action? Changed;

    /// <summary>
    /// The pages user code currently contributes. Rescans when the user assembly has been swapped
    /// since the last call, which is what makes the settings follow a recompile.
    /// </summary>
    public IReadOnlyList<UserSettingsPage> Pages
    {
        get
        {
            EnsureRefreshed();
            return pages;
        }
    }

    /// <summary>Rescans if the user assembly changed. Cheap when it has not.</summary>
    public void EnsureRefreshed()
    {
        var current = buildManager.ActiveUserAssembly;
        if (scannedOnce && ReferenceEquals(current, scanned)) return;

        scanned = current;
        scannedOnce = true;
        Rescan(current);
    }

    void Rescan(Assembly? assembly)
    {
        var previous = pages.Count;
        pages.Clear();

        if (assembly is not null) pages.AddRange(Scan(assembly, log));

        log.LogDebug("User settings: {Count} page(s) from {Assembly}", pages.Count,
            assembly?.GetName().Name ?? "no user assembly");

        if (previous != 0 || pages.Count != 0) Changed?.Invoke();
    }

    /// <summary>
    /// The pages an assembly contributes, without any editor state behind it. Separate from the
    /// catalog so the discovery rules can be exercised against any assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="log">Where a skipped type is reported.</param>
    /// <returns>One page per usable annotated class, in reflection order.</returns>
    public static IReadOnlyList<UserSettingsPage> Scan(Assembly assembly, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(log);

        var found = new List<UserSettingsPage>();

        foreach (var (type, attribute) in AnnotatedTypes(assembly, log))
        {
            if (string.IsNullOrWhiteSpace(attribute.Path)) continue;
            if (Create(type, log) is not { } target) continue;

            found.Add(new UserSettingsPage($"usercode.settings.{type.FullName}", attribute.Path.Trim(),
                target, attribute.Description, attribute.Order, attribute.Workspace));
        }

        return found;
    }

    /// <summary>
    /// Every annotated class in the assembly. A type that fails to load — one referencing a package
    /// that is no longer restored — is skipped rather than aborting the scan, matching how
    /// <c>TypeRegistry</c> treats the same failure.
    /// </summary>
    static IEnumerable<(Type Type, EditorSettingAttribute Attribute)> AnnotatedTypes(
        Assembly assembly, ILogger log)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.Where(type => type is not null)!];
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "User settings: could not read the types in {Assembly}", assembly.GetName().Name);
            yield break;
        }

        foreach (var type in types.Where(type => type is { IsClass: true, IsAbstract: false }))
        {
            EditorSettingAttribute? attribute = null;
            try
            {
                attribute = type.GetCustomAttribute<EditorSettingAttribute>(inherit: false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "User settings: could not read the attributes on {Type}", type.FullName);
            }

            if (attribute is not null) yield return (type, attribute);
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
