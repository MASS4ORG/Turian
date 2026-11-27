namespace Gaya.Plugin.Turian;

/// <summary>
/// Activates the <see cref="ILivePlugin"/>s of the project's editor assemblies when an assembly joins the set, and
/// disposes their lifetimes when it leaves: its brick is disabled, a recompile replaces it, or another project opens.
/// It is used on the UI thread.
/// </summary>
sealed class LivePluginBridge : IDisposable
{
    readonly Func<IReadOnlyList<Assembly>> assemblies;
    readonly Func<string?> project;
    readonly LivePluginContext context;
    readonly ILogger log;
    readonly EditorNotifications? notifications;

    // In activation order, so lifetimes are released newest first.
    readonly List<(Assembly Assembly, List<IDisposable> Lifetimes)> active = [];
    IReadOnlyList<Assembly>? applied;
    string? appliedProject;
    bool disposed;

    /// <summary>Creates the bridge; nothing is activated before the first <see cref="Sync"/>.</summary>
    /// <param name="assemblies">The editor assemblies in force, the same list until one joins or leaves.</param>
    /// <param name="project">The open project's folder; lifetimes never outlive the project they were made for.</param>
    /// <param name="context">The services and registries handed to every plugin.</param>
    /// <param name="log">Where a plugin that fails is reported.</param>
    /// <param name="notifications">Where a plugin that fails to activate is shown to the user.</param>
    public LivePluginBridge(Func<IReadOnlyList<Assembly>> assemblies, Func<string?> project,
        LivePluginContext context, ILogger log, EditorNotifications? notifications = null)
    {
        this.assemblies = assemblies;
        this.project = project;
        this.context = context;
        this.log = log;
        this.notifications = notifications;
    }

    /// <summary>Brings the active plugins in line with the assembly set. Cheap when nothing changed.</summary>
    public void Sync()
    {
        if (disposed)
        {
            return;
        }

        var currentProject = project();
        if (currentProject != appliedProject)
        {
            ReleaseAll();
            appliedProject = currentProject;
        }

        var current = assemblies();
        if (ReferenceEquals(current, applied))
        {
            return;
        }

        applied = current;
        for (var i = active.Count - 1; i >= 0; i--)
        {
            if (!current.Contains(active[i].Assembly))
            {
                var gone = active[i];
                active.RemoveAt(i);
                Release(gone.Assembly, gone.Lifetimes);
            }
        }

        foreach (var assembly in current)
        {
            if (active.All(entry => entry.Assembly != assembly))
            {
                active.Add((assembly, Activate(assembly)));
            }
        }
    }

    /// <summary>Releases every lifetime and stops following the assembly set.</summary>
    public void Dispose()
    {
        disposed = true;
        ReleaseAll();
    }

    void ReleaseAll()
    {
        var released = active.ToArray();
        active.Clear();
        applied = null;
        foreach (var (assembly, lifetimes) in released.Reverse())
        {
            Release(assembly, lifetimes);
        }
    }

    List<IDisposable> Activate(Assembly assembly)
    {
        var lifetimes = new List<IDisposable>();
        foreach (var type in PluginTypes(assembly))
        {
            try
            {
                var plugin = (ILivePlugin)Activator.CreateInstance(type)!;
                lifetimes.Add(plugin.Activate(context)
                              ?? throw new InvalidOperationException("Activate returned no lifetime."));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Editor plugin {Plugin} failed to activate", type.FullName);
                notifications?.Post(new EditorNotification(
                    $"Editor plugin {type.Name} failed to activate: {ex.Message}", IsError: true));
            }
        }

        return lifetimes;
    }

    void Release(Assembly assembly, List<IDisposable> lifetimes)
    {
        foreach (var lifetime in Enumerable.Reverse(lifetimes))
        {
            try
            {
                lifetime.Dispose();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "An editor plugin of {Assembly} failed to deactivate", assembly.GetName().Name);
            }
        }
    }

    IEnumerable<Type> PluginTypes(Assembly assembly)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Editor plugins: could not read the types in {Assembly}", assembly.GetName().Name);
            return [];
        }

        return types.OfType<Type>()
            .Where(static type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
                                  && typeof(ILivePlugin).IsAssignableFrom(type))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal);
    }
}
