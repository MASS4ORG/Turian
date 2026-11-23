using System.Runtime.Loader;

namespace Gaya.Host;

/// <summary>Reconciles live editor plugins on the workbench thread.</summary>
sealed class LivePluginHost : IDisposable
{
    readonly StudioBricks studio;
    readonly IProjectEditorBricks? project;
    readonly ILogger logger;
    readonly LivePluginContext context;
    readonly Dictionary<string, Activation> active = new(StringComparer.Ordinal);
    IReadOnlyList<ResolvedPackage> studioBricks = [];
    IReadOnlyList<ResolvedPackage>? previousStudio;
    IReadOnlyList<ResolvedPackage>? previousProject;

    internal LivePluginHost(GayaApplication app, StudioBricks studio, ILogger logger)
    {
        this.studio = studio;
        this.logger = logger;
        project = app.Services.GetService<IProjectEditorBricks>();
        context = new(app.Services, app.Panels, app.Commands, app.Menus);
        studio.Applied += ReadStudio;
        ReadStudio(true);
        Refresh();
    }

    void ReadStudio(bool applied)
    {
        if (!applied)
        {
            return;
        }
        try
        {
            Volatile.Write(ref studioBricks, studio.Resolve());
        }
        catch (PackageException ex)
        {
            logger.LogError("Live editor bricks could not be resolved: {Problem}", ex.Message);
        }
    }

    internal void Refresh()
    {
        var currentStudio = Volatile.Read(ref studioBricks);
        var currentProject = project?.EnabledBricks;
        if (ReferenceEquals(currentStudio, previousStudio) && ReferenceEquals(currentProject, previousProject))
        {
            return;
        }
        previousStudio = currentStudio;
        previousProject = currentProject;

        var wanted = new Dictionary<string, ResolvedPackage>(StringComparer.Ordinal);
        foreach (var brick in currentStudio.Concat(currentProject ?? []))
        {
            if (brick.Manifest.LivePlugin)
            {
                wanted[brick.Id] = brick;
            }
        }
        foreach (var (id, activation) in active.ToArray())
        {
            if (!wanted.ContainsKey(id))
            {
                active.Remove(id);
                Release(activation);
            }
        }
        foreach (var brick in wanted.Values)
        {
            ActivateIfChanged(brick);
        }
    }

    void ActivateIfChanged(ResolvedPackage brick)
    {
        Activation? activation = null;
        try
        {
            var paths = StudioBricks.LibraryDirectories(brick).Where(Directory.Exists)
                .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.dll"))
                .Order(StringComparer.Ordinal).ToArray();
            var fingerprint = string.Join('\n', paths.Select(static path =>
            {
                var file = new FileInfo(path);
                return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            }));
            if (active.TryGetValue(brick.Id, out var previous))
            {
                if (previous.Fingerprint == fingerprint)
                {
                    return;
                }
                active.Remove(brick.Id);
                Release(previous);
            }
            var loadContext = new PluginLoadContext(paths);
            activation = new Activation(brick, fingerprint, loadContext);
            foreach (var path in paths)
            {
                var assembly = loadContext.LoadFromAssemblyName(AssemblyName.GetAssemblyName(path));
                foreach (var type in assembly.GetTypes())
                {
                    if (type is { IsClass: true, IsAbstract: false } && typeof(ILivePlugin).IsAssignableFrom(type))
                    {
                        var plugin = (ILivePlugin)Activator.CreateInstance(type)!;
                        activation.Lifetimes.Add(plugin.Activate(context));
                    }
                }
            }
            if (activation.Lifetimes.Count == 0)
            {
                logger.LogWarning("Editor brick {Brick} has no compiled live plugin", brick.Id);
                loadContext.Unload();
                return;
            }
            active.Add(brick.Id, activation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Editor brick {Brick} failed to activate", brick.Id);
            if (activation is not null)
            {
                Release(activation);
            }
        }
    }

    void Release(Activation activation)
    {
        foreach (var lifetime in Enumerable.Reverse(activation.Lifetimes))
        {
            try
            {
                lifetime.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Editor brick {Brick} failed to deactivate", activation.Brick.Id);
            }
        }
        activation.LoadContext.Unload();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        studio.Applied -= ReadStudio;
        foreach (var activation in active.Values.Reverse())
        {
            Release(activation);
        }
        active.Clear();
    }

    sealed record Activation(ResolvedPackage Brick, string Fingerprint, PluginLoadContext LoadContext)
    {
        internal List<IDisposable> Lifetimes { get; } = [];
    }

    sealed class PluginLoadContext(string[] paths) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = Array.Find(paths, path => Path.GetFileNameWithoutExtension(path) == assemblyName.Name);
            if (path is null)
            {
                return null;
            }
            using var assembly = File.OpenRead(path);
            return LoadFromStream(assembly);
        }
    }
}
