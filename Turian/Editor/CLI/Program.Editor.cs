using Gaya.Sdk;

namespace Turian.Editor.CLI;

public static partial class Program
{
    const string systemDefaultName = "default";

    static Command EditorCommand(Argument<FileSystemInfo> projectArg)
    {
        var json = new Option<bool>("--json") { Description = "Print machine-readable JSON", Recursive = true };
        var scope = new Option<string?>("--scope")
        {
            Description = "personal (the default when the project has an identity), user, workspace or studio",
        };
        var command = new Command("editor",
            "The external editor Studio opens code in: list installations, show or change the preference, test it")
        {
            json,
        };
        command.Subcommands.Add(EditorListCommand(json));
        command.Subcommands.Add(EditorGetCommand(projectArg, json));
        command.Subcommands.Add(EditorSetCommand(projectArg, scope));
        command.Subcommands.Add(EditorResetCommand(projectArg, scope));
        command.Subcommands.Add(EditorTestCommand(projectArg));
        return command;
    }

    static Command EditorListCommand(Option<bool> json)
    {
        var command = new Command("list", "List the editor installations found on this machine");
        command.SetAction(parsed =>
        {
            var found = new ExternalEditorRegistry(Log.Logger).DiscoverAll();
            if (parsed.GetValue(json))
            {
                Console.WriteLine(JsonSerializer.Serialize(found.Select(static installation => new
                {
                    editor = installation.EditorId,
                    name = installation.DisplayName,
                    version = installation.Version,
                    executable = installation.Executable,
                }), EditorJson));
                return 0;
            }

            foreach (var installation in found)
            {
                Console.WriteLine($"{installation.EditorId}\t{installation.DisplayName}\t{installation.Version}\t"
                                  + installation.Executable);
            }

            return 0;
        });
        return command;
    }

    static Command EditorGetCommand(Argument<FileSystemInfo> projectArg, Option<bool> json)
    {
        var command = new Command("get", "Show the external editor that applies to the project and where it is stored")
        {
            projectArg,
        };
        command.SetAction(parsed =>
        {
            var (preference, project) = OpenPreference(parsed.GetValue(projectArg)!);
            var (choice, scope) = preference.Resolve();
            var status = choice.Check(new ExternalEditorRegistry(Log.Logger));
            if (parsed.GetValue(json))
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    editor = choice.UsesSystemDefault ? systemDefaultName : choice.EditorId,
                    executable = choice.Executable,
                    arguments = choice.Arguments,
                    options = choice.Options ?? new Dictionary<string, string>(),
                    scope = scope?.ToString().ToLowerInvariant(),
                    path = scope is { } held ? preference.PathFor(held) : null,
                    status = status.ToString(),
                    project,
                }, EditorJson));
                return 0;
            }

            Console.WriteLine($"editor: {(choice.UsesSystemDefault ? systemDefaultName : choice.EditorId)}");
            Console.WriteLine($"executable: {choice.Executable}");
            Console.WriteLine($"arguments: {choice.Arguments}");
            Console.WriteLine($"scope: {scope?.ToString().ToLowerInvariant() ?? "none"}");
            Console.WriteLine($"status: {status}");
            return 0;
        });
        return command;
    }

    static Command EditorSetCommand(Argument<FileSystemInfo> projectArg, Option<string?> scopeOption)
    {
        var editorArg = new Argument<string>("Editor")
        {
            Description = $"An editor id from 'editor list', 'custom', or '{systemDefaultName}' for the operating system's program",
        };
        var path = new Option<FileInfo?>("--path") { Description = "The program to start; the newest installation is used when omitted" };
        var arguments = new Option<string?>("--arguments")
        {
            Description = $"The argument template of a custom editor, using {string.Join(", ", EditorArgumentTemplate.Tokens)}",
        };
        var options = new Option<string[]>("--option")
        {
            Description = "An editor option as key=value; may be repeated",
            AllowMultipleArgumentsPerToken = false,
        };
        var command = new Command("set", "Choose the external editor for a scope")
        {
            editorArg, projectArg, path, arguments, options, scopeOption,
        };
        command.SetAction(parsed =>
        {
            var (preference, _) = OpenPreference(parsed.GetValue(projectArg)!);
            if (ParseScope(parsed.GetValue(scopeOption), preference) is not { } scope)
            {
                return 1;
            }

            var registry = new ExternalEditorRegistry(Log.Logger);
            if (BuildChoice(registry, parsed.GetValue(editorArg)!, parsed.GetValue(path)?.FullName,
                    parsed.GetValue(arguments), parsed.GetValue(options) ?? []) is not { } choice)
            {
                return 1;
            }

            if (!preference.Write(scope, choice))
            {
                Log.Logger.LogError("The {Scope} scope could not be written to {Path}", scope, preference.PathFor(scope));
                return 1;
            }

            Console.WriteLine($"{scope.ToString().ToLowerInvariant()}: {preference.PathFor(scope)}");
            return 0;
        });
        return command;
    }

    static Command EditorResetCommand(Argument<FileSystemInfo> projectArg, Option<string?> scopeOption)
    {
        var all = new Option<bool>("--all") { Description = "Clear the preference from every scope" };
        var command = new Command("reset", "Remove the external editor preference from a scope, so the scope below applies")
        {
            projectArg, scopeOption, all,
        };
        command.SetAction(parsed =>
        {
            var (preference, _) = OpenPreference(parsed.GetValue(projectArg)!);
            IEnumerable<SettingsScope> scopes;
            if (parsed.GetValue(all))
            {
                scopes = preference.Scopes.ToList();
            }
            else if (ParseScope(parsed.GetValue(scopeOption), preference) is { } one)
            {
                scopes = [one];
            }
            else
            {
                return 1;
            }

            var failed = scopes.Where(scope => !preference.Clear(scope)).ToList();
            foreach (var scope in failed)
            {
                Log.Logger.LogError("The {Scope} scope could not be cleared at {Path}", scope, preference.PathFor(scope));
            }

            return failed.Count == 0 ? 0 : 1;
        });
        return command;
    }

    static Command EditorTestCommand(Argument<FileSystemInfo> projectArg)
    {
        var file = new Option<FileInfo?>("--file") { Description = "A file to open instead of only the project" };
        var line = new Option<int?>("--line") { Description = "The line to place the caret on" };
        var command = new Command("test", "Open the project, or a file in it, with the external editor that applies")
        {
            projectArg, file, line,
        };
        command.SetAction(async (parsed, cancellation) =>
        {
            var settings = CreateSettings(parsed.GetValue(projectArg)!);
            var registry = new ExternalEditorRegistry(Log.Logger);
            var notifications = new EditorNotifications();
            var launcher = new ExternalEditorLauncher(registry, notifications, Log.Logger)
            {
                Selection = new ExternalEditorPreference(settings.ProjectAbsoluteDir).Resolve().Choice.ToSelection(),
            };
            var solution = ProjectRegeneration.SolutionPath(settings);
            var context = new EditorContext(settings.ProjectAbsoluteDir, File.Exists(solution) ? solution : null,
                parsed.GetValue(file)?.FullName, parsed.GetValue(line));

            var opened = await launcher.OpenAsync(context, cancellation).ConfigureAwait(false);
            while (notifications.TryTake(out var notification))
            {
                Console.Error.WriteLine(notification.Message);
            }

            return opened ? 0 : 1;
        });
        return command;
    }

    /// <summary>The choice a <c>set</c> command stores, or <c>null</c> after reporting why it cannot be built.</summary>
    static ExternalEditorChoice? BuildChoice(IExternalEditorRegistry registry, string editorId, string? executable,
        string? arguments, string[] optionPairs)
    {
        if (string.Equals(editorId, systemDefaultName, StringComparison.OrdinalIgnoreCase))
        {
            return new ExternalEditorChoice();
        }

        return FindEditor(registry, editorId) is { } editor && ArgumentsFit(editor, arguments)
            ? Compose(registry, editor, executable, arguments, optionPairs)
            : null;
    }

    static bool ArgumentsFit(IExternalEditor editor, string? arguments)
    {
        if (arguments is null || editor is CustomEditor)
        {
            return true;
        }

        Log.Logger.LogError("--arguments applies to the custom editor only");
        return false;
    }

    static ExternalEditorChoice? Compose(IExternalEditorRegistry registry, IExternalEditor editor, string? executable,
        string? arguments, string[] optionPairs)
    {
        if (ProgramFor(registry, editor, executable) is not { } program || ParseOptions(editor, optionPairs) is not { } values)
        {
            return null;
        }

        return new ExternalEditorChoice(editor.Id, program, arguments ?? string.Empty, values.Count == 0 ? null : values);
    }

    static IExternalEditor? FindEditor(IExternalEditorRegistry registry, string editorId)
    {
        var editor = registry.Find(editorId);
        if (editor is null)
        {
            Log.Logger.LogError("No editor '{Editor}'. Known editors: {Known}, {Default}", editorId,
                string.Join(", ", registry.Editors.Select(static known => known.Id)), systemDefaultName);
        }

        return editor;
    }

    /// <summary>The program to store: the one given, else the editor's first discovered installation.</summary>
    static string? ProgramFor(IExternalEditorRegistry registry, IExternalEditor editor, string? executable)
    {
        var program = executable ?? registry.DiscoverAll().FirstOrDefault(found =>
            string.Equals(found.EditorId, editor.Id, StringComparison.OrdinalIgnoreCase))?.Executable;
        if (program is null)
        {
            Log.Logger.LogError("No installation of {Editor} was found; pass --path", editor.DisplayName);
        }
        else if (!File.Exists(program))
        {
            Log.Logger.LogWarning("{Path} does not exist; Studio will warn until it does", program);
        }

        return program;
    }

    static Dictionary<string, string>? ParseOptions(IExternalEditor editor, string[] pairs)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            var key = split > 0 ? pair[..split] : string.Empty;
            if (editor.Options.FirstOrDefault(option => option.Key == key) is not { } known)
            {
                Log.Logger.LogError("{Editor} has no option '{Option}'. Options: {Known}", editor.DisplayName, pair,
                    string.Join(", ", editor.Options.Select(static option => option.Key)));
                return null;
            }

            var value = pair[(split + 1)..];
            if (!known.Accepts(value))
            {
                Log.Logger.LogError("{Editor} option '{Option}' does not take '{Value}'. Use one of: {Values}",
                    editor.DisplayName, key, value, string.Join(", ", (known.Choices ?? []).Select(static choice => choice.Value)));
                return null;
            }

            values[key] = value;
        }

        return values;
    }

    /// <summary>The scope an option names, or the default one: personal when available, otherwise the user's.</summary>
    static SettingsScope? ParseScope(string? name, ExternalEditorPreference preference)
    {
        if (name is null)
        {
            return preference.Scopes.Contains(SettingsScope.Personal) ? SettingsScope.Personal : SettingsScope.User;
        }

        if (!Enum.TryParse<SettingsScope>(name, ignoreCase: true, out var scope) || scope == SettingsScope.None
            || !preference.Scopes.Contains(scope))
        {
            Log.Logger.LogError("'{Scope}' is not a scope this project can use. Available: {Available}", name,
                string.Join(", ", preference.Scopes.Select(static available => available.ToString().ToLowerInvariant())));
            return null;
        }

        return scope;
    }

    static (ExternalEditorPreference Preference, string Project) OpenPreference(FileSystemInfo project)
    {
        var settings = CreateSettings(project);
        return (new ExternalEditorPreference(settings.ProjectAbsoluteDir,
            report: static (message, error) => Log.Logger.LogWarning(error, "{Message}", message)),
            settings.ProjectAbsoluteDir);
    }

    static readonly JsonSerializerOptions EditorJson = new() { WriteIndented = true };
}
