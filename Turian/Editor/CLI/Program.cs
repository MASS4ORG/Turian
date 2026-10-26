using Codeuctivity;

namespace Turian.Editor.CLI;

/// <summary>
/// CLI entry point for the editor's headless automation surface: build, play and diagnostics.
/// </summary>
public static partial class Program
{
    /// <summary>Entry point.</summary>
    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(static builder => builder
#if DEBUG
            .SetMinimumLevel(LogLevel.Debug)
#else
            .SetMinimumLevel(LogLevel.Information)
#endif
            .AddConsole());
        Log.Configure(loggerFactory);

        BuildManager.MsBuildLocatorRegisterDefaults();

        var projectArg = ProjectPathArgument();
        var configOption = ConfigurationOption();

        var root = new RootCommand("Turian CLI — create, build, play and headless diagnostics")
        {
            NewCommand(),
            CompileCommand(projectArg),
            ExportCommand(projectArg, configOption),
            PlayCommand(projectArg),
            PlayModeCommand(projectArg),
            SceneCommand(projectArg),
            ScreenshotCommand(projectArg),
            PickCommand(projectArg),
            OapCommand(projectArg),
            BrickCommand(),
            VariantCommand(),
            ImportCommand(),
            ThemeCommand()
        };

        return await root.Parse(args).InvokeAsync();
    }

    // ── Arguments & options ────────────────────────────────────────────────────

    static Argument<FileSystemInfo> ProjectPathArgument()
    {
        var arg = new Argument<FileSystemInfo>("ProjectPath")
        {
            Description = "Project folder (a file inside it, such as an older project.data, also works)",
            DefaultValueFactory = static _ => new DirectoryInfo("./")
        };

        arg.Validators.Add(static result =>
        {
            var info = result.GetValue<FileSystemInfo>("ProjectPath");
            if (info is null)
            {
                result.AddError("Not a valid project path");
                return;
            }

            foreach (var issue in ProjectValidator.Validate(info.FullName)
                         .Where(static issue => issue.Severity == ProjectIssueSeverity.Error))
            {
                result.AddError(issue.Message);
            }
        });

        return arg;
    }

    static Option<BuildConfiguration> ConfigurationOption() =>
        new(
            "--configuration",
            "-c");

    // ── Commands ───────────────────────────────────────────────────────────────

    static Command NewCommand()
    {
        var pathArg = new Argument<DirectoryInfo>("ProjectPath")
        {
            Description = "Folder to create the project in; it must not exist yet or be empty"
        };
        var cmd = new Command("new", "Create a new project") { pathArg };
        cmd.SetAction(async (result, _) =>
        {
            var directory = result.GetValue(pathArg)!;
            if (directory.Exists && directory.EnumerateFileSystemInfos().Any())
            {
                Log.Logger.LogError("{Path} already exists and is not empty", directory.FullName);
                return 1;
            }

            var created = await new ProjectBootstrapper().CreateAsync(directory.FullName).ConfigureAwait(false);
            if (created is null)
            {
                return 1;
            }

            Log.Logger.LogInformation("Created project {Path}", created);
            return 0;
        });
        return cmd;
    }

    static Command CompileCommand(Argument<FileSystemInfo> projectArg)
    {
        var cmd = new Command("compile", "Compile the user project as a library") { projectArg };
        cmd.SetAction(async (result, _) =>
        {
            var settings = CreateSettings(result.GetValue(projectArg)!);
            Log.Logger.LogInformation("Compiling {Title}…", settings.Title);

            using var preparation = await PrepareProjectAsync(settings, reimport: true, loadUserCode: false)
                .ConfigureAwait(false);

            // Compile into a temp directory (no slot management needed for one-shot CLI)
            var slotDir = Path.Combine(settings.CacheAbsoluteDir, "bin", "CLI");
            Directory.CreateDirectory(slotDir);

            var compiler = new CompileUserCode(settings, Log.Logger, slotDir);
            var path = await compiler.ExecuteAsync();
            Log.Logger.LogInformation("Output: {Path}", path);
        });
        return cmd;
    }

    static Command ExportCommand(
        Argument<FileSystemInfo> projectArg,
        Option<BuildConfiguration> configOption)
    {
        var profileOption = new Option<string[]>("--profile")
        {
            Description = "Build profile to export, into Output/<profile>/<game>; repeat it to build several in one run. "
                          + "Without it, export the active profile into Output/<game>"
        };
        var cmd = new Command("export", "Build and export as a standalone packaged game")
        {
            projectArg, configOption, profileOption
        };

        cmd.SetAction(async (result, _) =>
        {
            var settings = CreateSettings(result.GetValue(projectArg)!);
            var config = result.GetValue(configOption);
            var profiles = (result.GetValue(profileOption) ?? [])
                .Select(name => BuildProfiles.Find(settings, name)).ToList();
            Log.Logger.LogInformation("Exporting {Title} [{Config}]…", settings.Title, config);

            using var preparation = await PrepareProjectAsync(settings, reimport: true, loadUserCode: false)
                .ConfigureAwait(false);

            // Compile library first
            var slotDir = Path.Combine(settings.CacheAbsoluteDir, "bin", "CLI");
            Directory.CreateDirectory(slotDir);
            var compiler = new CompileUserCode(settings, Log.Logger, slotDir);
            await compiler.ExecuteAsync();

            if (profiles.Count == 0)
            {
                await new ExportUserCode(settings, Log.Logger, config, BuildProfiles.Active(settings)).ExecuteAsync();
                return;
            }

            foreach (var profile in profiles)
            {
                Log.Logger.LogInformation("Build profile {Profile}: {Target}", profile.Name, profile.Profile.Target);
                var output = Path.Combine(settings.ProjectAbsoluteDir, "Output", profile.Name.SanitizeFilename(' '),
                    settings.TitleToPathFriendly ?? "Game");
                await new ExportUserCode(settings, Log.Logger, config, profile, output).ExecuteAsync();
            }
        });

        return cmd;
    }

    // ── oap: Open Asset Package tools ──────────────────────────────────────────

}
