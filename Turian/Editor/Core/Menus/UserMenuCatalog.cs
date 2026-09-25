namespace Turian.Editor.Core;

/// <summary>
/// Discovers the menu entries user code contributes with <see cref="MenuItemAttribute"/>:
/// a <c>public static</c> method annotated with a path becomes a main-menu item that runs it.
/// Framework-agnostic — it yields paths and delegates, and a shell turns those into whatever its menu
/// bar is made of.
/// </summary>
/// <remarks>
/// An annotated method must take either no parameters, or exactly one the editor's service provider
/// can supply. Anything else is skipped with a warning rather than failing the scan, so one bad
/// signature cannot cost the project its whole menu.
/// </remarks>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class UserMenuCatalog
{
    readonly BuildManager buildManager;
    readonly ILogger log;

    readonly List<UserMenuCommand> commands = [];
    Assembly? scanned;
    bool scannedOnce;

    /// <summary>Creates the catalog over the build manager that owns the user assembly.</summary>
    /// <param name="buildManager">Supplies the currently loaded user assembly.</param>
    /// <param name="log">Where a bad signature is reported.</param>
    public UserMenuCatalog(BuildManager buildManager, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(buildManager);
        ArgumentNullException.ThrowIfNull(log);

        this.buildManager = buildManager;
        this.log = log;
    }

    /// <summary>
    /// Raised after a rescan produced a different set of entries, so a shell can re-register them.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// The entries user code currently contributes. Rescans when the user assembly has been swapped
    /// since the last call, which is what makes the menu follow a recompile.
    /// </summary>
    public IReadOnlyList<UserMenuCommand> Commands
    {
        get
        {
            EnsureRefreshed();
            return commands;
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
        var previous = commands.Count;
        commands.Clear();

        if (assembly is not null) commands.AddRange(Scan(assembly, log));

        log.LogDebug("User menu: {Count} item(s) from {Assembly}", commands.Count,
            assembly?.GetName().Name ?? "no user assembly");

        if (previous != 0 || commands.Count != 0) Changed?.Invoke();
    }

    /// <summary>
    /// The entries an assembly contributes, without any editor state behind it. Separate from the
    /// catalog so the discovery rules can be exercised against any assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="log">Where a skipped method is reported.</param>
    /// <returns>One entry per usable annotated method, in reflection order.</returns>
    public static IReadOnlyList<UserMenuCommand> Scan(Assembly assembly, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(log);

        var found = new List<UserMenuCommand>();

        foreach (var method in AnnotatedMethods(assembly, log))
        {
            if (method.GetCustomAttribute<MenuItemAttribute>(inherit: false) is not { } attribute) continue;
            if (string.IsNullOrWhiteSpace(attribute.Path)) continue;
            if (!TryBind(method, log, out var invoke)) continue;

            found.Add(new UserMenuCommand(attribute.Path.Trim(), Identify(method), invoke));
        }

        return found;
    }

    /// <summary>
    /// Every <c>public static</c> method in the assembly carrying the attribute. A type that fails to
    /// load — a component referencing a package that is no longer restored — is skipped rather than
    /// aborting the scan, matching how <c>TypeRegistry</c> treats the same failure.
    /// </summary>
    static IEnumerable<MethodInfo> AnnotatedMethods(Assembly assembly, ILogger log)
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
            log.LogWarning(ex, "User menu: could not read the types in {Assembly}", assembly.GetName().Name);
            yield break;
        }

        foreach (var type in types)
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "User menu: could not read the methods on {Type}", type.FullName);
                continue;
            }

            foreach (var method in methods)
            {
                var annotated = false;
                try
                {
                    annotated = method.GetCustomAttribute<MenuItemAttribute>(inherit: false) is not null;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "User menu: could not read the attributes on {Method}", method.Name);
                }

                if (annotated) yield return method;
            }
        }
    }

    /// <summary>
    /// Builds the invoker for an annotated method, rejecting a signature the editor cannot call.
    /// </summary>
    static bool TryBind(MethodInfo method, ILogger log, out Action<IServiceProvider> invoke)
    {
        invoke = _ => { };
        var parameters = method.GetParameters();

        if (parameters.Length > 1)
        {
            log.LogWarning("User menu: {Type}.{Method} takes {Count} parameters; it must take none or one",
                method.DeclaringType?.Name, method.Name, parameters.Length);
            return false;
        }

        var parameterType = parameters.Length == 1 ? parameters[0].ParameterType : null;
        var name = $"{method.DeclaringType?.Name}.{method.Name}";
        var logger = log;

        invoke = services =>
        {
            try
            {
                // `object` is the catch-all parameter the Avalonia studio used to hand its window to;
                // the closest thing this shell has is the provider itself.
                object?[] arguments = parameterType is null
                    ? []
                    : [parameterType == typeof(IServiceProvider) || parameterType == typeof(object)
                        ? services
                        : services.GetService(parameterType)];

                if (parameterType is not null && arguments[0] is null)
                {
                    logger.LogWarning("User menu: {Method} wants a {Parameter} and the editor has none",
                        name, parameterType.Name);
                    return;
                }

                method.Invoke(null, arguments);
            }
            catch (TargetInvocationException ex)
            {
                logger.LogError(ex.InnerException ?? ex, "User menu: {Method} threw", name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "User menu: {Method} could not be invoked", name);
            }
        };

        return true;
    }

    /// <summary>A stable id for the method, so re-registering after a recompile replaces rather than duplicates.</summary>
    static string Identify(MethodInfo method) =>
        $"usercode.menu.{method.DeclaringType?.FullName}.{method.Name}";
}
