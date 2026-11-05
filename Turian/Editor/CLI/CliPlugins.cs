using System.Reflection;
using MASS4.Bricks;
using Turian.Cli.Sdk;

namespace Turian.Editor.CLI;

/// <summary>Loads command providers from the selected project's bricks and the user's studio bricks.</summary>
static class CliPlugins
{
    internal static void Load(RootCommand root, string[] args, ILogger logger, StudioBricks? studio = null,
        string? currentDirectory = null)
    {
        var project = FindProject(args, currentDirectory ?? Directory.GetCurrentDirectory());
        var registry = new CommandRegistry(root);
        var packages = new List<(ResolvedPackage Brick, string? Workspace)>();
        if (project is not null)
        {
            Resolve(() => ProjectPackages.Resolve(project).Packages, project);
        }

        studio ??= TurianStudioBricks.Workspace();
        if (studio.HasManifest)
        {
            Resolve(studio.Resolve, studio.Root);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (brick, workspace) in packages)
        {
            if (!seen.Add(brick.Id))
            {
                continue;
            }

            LoadBrick(registry, brick, workspace, project, logger);
        }

        return;

        void Resolve(Func<IReadOnlyList<ResolvedPackage>> resolve, string workspace)
        {
            try
            {
                packages.AddRange(resolve().Select(brick => (brick, (string?)workspace)));
            }
            catch (PackageException ex)
            {
                logger.LogWarning("CLI plugins in {Workspace} could not be resolved: {Problem}", workspace, ex.Message);
            }
        }
    }

    static void LoadBrick(CommandRegistry registry, ResolvedPackage brick, string? workspace,
        string? project, ILogger logger)
    {
        try
        {
            BrickAssemblies.Load([brick], logger, workspace);
            var paths = BrickAssemblies.EditorAssemblies(brick).Concat(BrickAssemblies.RuntimeAssemblies(brick));
            var names = paths.Select(static path => Path.GetFileNameWithoutExtension(path))
                .ToHashSet(StringComparer.Ordinal);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => names.Contains(assembly.GetName().Name ?? string.Empty));
            foreach (var assembly in assemblies)
            {
                RegisterAssembly(registry, assembly, new CliPluginContext(brick.RootPath, project, logger));
            }
        }
        catch (Exception ex) when (ex is PackageException or IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "CLI plugins from {Brick} could not be loaded", brick.Id);
        }
    }

    internal static string? FindProject(IReadOnlyList<string> args, string currentDirectory)
    {
        for (var index = 0; index < args.Count && args[index] != "--"; index++)
        {
            if (args[index].StartsWith("--cli-project=", StringComparison.Ordinal))
            {
                return Path.GetFullPath(args[index][14..]);
            }

            if (args[index] == "--cli-project" && index + 1 < args.Count)
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        for (var directory = new DirectoryInfo(currentDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ProjectManifest.DirectoryName, ProjectManifest.FileName)))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    internal static void RegisterAssembly(CommandRegistry registry, Assembly assembly, CliPluginContext context)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            context.Logger.LogWarning(ex, "CLI plugin assembly {Assembly} has unavailable dependencies", assembly.GetName().Name);
            return;
        }

        foreach (var type in types.Where(static type => type is { IsAbstract: false, IsInterface: false, IsVisible: true }
                     && typeof(ICliPlugin).IsAssignableFrom(type)).OrderBy(static type => type.FullName, StringComparer.Ordinal))
        {
            try
            {
                var plugin = (ICliPlugin)Activator.CreateInstance(type)!;
                registry.Add(plugin.CreateCommands(context).ToArray());
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(ex, "CLI plugin {Plugin} could not register its commands", type.FullName);
            }
        }
    }

    /// <summary>Keeps built-in commands reserved and applies each provider's registrations atomically.</summary>
    internal sealed class CommandRegistry(RootCommand root)
    {
        readonly Dictionary<string, Command> commands = root.Subcommands
            .SelectMany(static command => command.Aliases.Prepend(command.Name).Select(name => (name, command)))
            .ToDictionary(static entry => entry.name, static entry => entry.command, StringComparer.Ordinal);
        readonly HashSet<string> groups = new(StringComparer.Ordinal);

        internal void Add(IReadOnlyList<CliCommand> registrations)
        {
            var planned = new Dictionary<string, Command>(commands, StringComparer.Ordinal);
            var plannedGroups = new HashSet<string>(groups, StringComparer.Ordinal);
            var additions = new List<(Command Parent, Command Child)>();
            foreach (var registration in registrations)
            {
                ArgumentNullException.ThrowIfNull(registration.Command);
                var (parent, prefix) = ParentFor(registration.ParentPath);

                foreach (var name in registration.Command.Aliases.Prepend(registration.Command.Name))
                {
                    if (!planned.TryAdd(prefix + name, registration.Command))
                    {
                        throw new InvalidOperationException($"CLI command '{name}' is already registered.");
                    }
                }

                additions.Add((parent, registration.Command));
            }

            foreach (var (parent, child) in additions)
            {
                parent.Subcommands.Add(child);
            }

            commands.Clear();
            foreach (var (name, command) in planned)
            {
                commands.Add(name, command);
            }

            groups.UnionWith(plannedGroups);

            return;

            (Command Parent, string Prefix) ParentFor(IReadOnlyList<string> path)
            {
                var parent = (Command)root;
                var prefix = string.Empty;
                foreach (var name in path)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(name);
                    var key = prefix + name;
                    if (!planned.TryGetValue(key, out var group))
                    {
                        group = new Command(name);
                        planned.Add(key, group);
                        plannedGroups.Add(key);
                        additions.Add((parent, group));
                    }
                    else if (!plannedGroups.Contains(key))
                    {
                        throw new InvalidOperationException(
                            $"CLI group '{string.Join(' ', path)}' conflicts with an existing command.");
                    }

                    parent = group;
                    prefix = key + "\0";
                }

                return (parent, prefix);
            }
        }
    }
}
