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
            DeployCommand(projectArg, configOption),
            DevicesCommand(projectArg),
            PlayCommand(projectArg),
            PlayModeCommand(projectArg),
            SceneCommand(projectArg),
            ScreenshotCommand(projectArg),
            PickCommand(projectArg),
            OapCommand(projectArg),
            BrickCommand(),
            VariantCommand(),
            ThemeCommand()
        };

        root.Options.Add(new Option<DirectoryInfo?>("--cli-project")
        {
            Description = "Load CLI plugins from this project's bricks (defaults to the current project)",
            Recursive = true,
        });
        CliPlugins.Load(root, args, Log.Logger);
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
            Description = "Folder to create the project in; it must be empty or contain only repository metadata"
        };
        var cmd = new Command("new", "Create a new project") { pathArg };
        cmd.SetAction(async (result, _) =>
        {
            var directory = result.GetValue(pathArg)!;
            var existing = directory.Exists ? directory.EnumerateFileSystemInfos().ToArray() : [];
            if (existing.Any(static entry => !IsRepositoryMetadata(entry)))
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
            foreach (var entry in existing)
            {
                Log.Logger.LogInformation("Kept existing {Path}", entry.FullName);
            }

            return 0;
        });
        return cmd;
    }

    static bool IsRepositoryMetadata(FileSystemInfo entry)
    {
        // A worktree can use a .git file instead of a directory. Never follow metadata links.
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        if (entry.Name == ".git")
        {
            return true;
        }

        if (entry is DirectoryInfo)
        {
            return entry.Name == ".github";
        }

        return entry.Name is ".gitattributes" or ".gitignore" or "README.md" or ".gitlab-ci.yml"
               || entry.Name.StartsWith("LICENSE", StringComparison.Ordinal);
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
        var installOption = new Option<bool>("--install")
        {
            Description = "Install the export on a connected device and start it, for platforms that run on devices "
                          + "such as Android"
        };
        var deviceOption = new Option<string?>("--device")
        {
            Description = "The device to install on, as the platform lists it (for Android, the serial adb devices "
                          + "shows); implies --install. Without it, the device from the user settings, else the only "
                          + "one connected"
        };
        var cmd = new Command("export", "Build and export as a standalone packaged game")
        {
            projectArg, configOption, profileOption, installOption, deviceOption
        };

        cmd.SetAction(async (result, _) =>
        {
            var device = result.GetValue(deviceOption);
            var deployment = result.GetValue(installOption) || device is not null ? new BuildDeployment(device) : null;
            await ExportAsync(result.GetValue(projectArg)!, result.GetValue(configOption),
                result.GetValue(profileOption) ?? [], deployment).ConfigureAwait(false);
        });

        return cmd;
    }

    /// <summary>Exports a project with each named profile, or its active one, installing each build when asked.</summary>
    static async Task<List<ExportUserCode>> ExportAsync(FileSystemInfo project, BuildConfiguration config,
        IReadOnlyList<string> profileNames, BuildDeployment? deployment)
    {
        var settings = CreateSettings(project);
        var profiles = profileNames.Select(name => BuildProfiles.Find(settings, name)).ToList();
        Log.Logger.LogInformation("Exporting {Title} [{Config}]…", settings.Title, config);

        using var preparation = await PrepareProjectAsync(settings, reimport: true, loadUserCode: false)
            .ConfigureAwait(false);
        // After the project's bricks are loaded, since platforms such as Android come from them.
        LoadUserSettings();

        // Compile library first
        var slotDir = Path.Combine(settings.CacheAbsoluteDir, "bin", "CLI");
        Directory.CreateDirectory(slotDir);
        var compiler = new CompileUserCode(settings, Log.Logger, slotDir);
        await compiler.ExecuteAsync();

        if (profiles.Count == 0)
        {
            var export = new ExportUserCode(settings, Log.Logger, config, BuildProfiles.Active(settings),
                deployment: deployment);
            await export.ExecuteAsync();
            return [export];
        }

        List<ExportUserCode> exports = [];
        foreach (var profile in profiles)
        {
            Log.Logger.LogInformation("Build profile {Profile}: {Target}", profile.Name, profile.Profile.Target);
            var output = Path.Combine(settings.ProjectAbsoluteDir, "Output", profile.Name.SanitizeFilename(' '),
                settings.TitleToPathFriendly ?? "Game");
            var export = new ExportUserCode(settings, Log.Logger, config, profile, output, deployment);
            await export.ExecuteAsync();
            exports.Add(export);
        }

        return exports;
    }

    /// <summary>The same per-user choices Studio saved, such as where the Android SDK is.</summary>
    static void LoadUserSettings()
    {
        foreach (var page in BuildPlatforms.All().Select(static platform => platform.UserSettings).OfType<object>())
        {
            UserSettingsFile.Load(page);
        }
    }

    static Command DeployCommand(
        Argument<FileSystemInfo> projectArg,
        Option<BuildConfiguration> configOption)
    {
        var profileOption = new Option<string?>("--profile")
        {
            Description = "Build profile to deploy; without it, the active profile"
        };
        var deviceOption = new Option<string?>("--device")
        {
            Description = "The device to install on, as turian devices lists it. Without it, the device from the user "
                          + "settings, else the only one connected"
        };
        var cmd = new Command("deploy",
            "Export, install on a device, start the game and show its log until it exits; Ctrl+C stops it")
        {
            projectArg, configOption, profileOption, deviceOption
        };

        cmd.SetAction(async (result, cancellationToken) =>
        {
            var profile = result.GetValue(profileOption);
            var exports = await ExportAsync(result.GetValue(projectArg)!, result.GetValue(configOption),
                profile is null ? [] : [profile], new BuildDeployment(result.GetValue(deviceOption)))
                .ConfigureAwait(false);
            if (exports.Single().Deployed is { } deployed)
            {
                await deployed.Platform.FollowAsync(deployed.Context, deployed.Device, Console.WriteLine,
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return cmd;
    }

    static Command DevicesCommand(Argument<FileSystemInfo> projectArg)
    {
        var cmd = new Command("devices",
            "List the devices each export platform of a project can install on; platforms come from its bricks")
        {
            projectArg
        };
        cmd.SetAction(result =>
        {
            var settings = CreateSettings(result.GetValue(projectArg)!);
            BrickAssemblies.Load(ProjectPackages.ResolveOrEmpty(settings.ProjectAbsoluteDir), Log.Logger,
                settings.ProjectAbsoluteDir);
            LoadUserSettings();
            foreach (var platform in BuildPlatforms.All().Where(static platform => platform.Deploys))
            {
                var status = platform.CheckToolchain();
                var devices = platform.Devices ?? [];
                Console.WriteLine($"{platform.Id}:");
                if (!status.IsReady)
                {
                    Console.WriteLine($"  {status.Message}");
                }

                if (devices.Count == 0)
                {
                    Console.WriteLine("  no device ready");
                }

                foreach (var device in devices)
                {
                    Console.WriteLine($"  {device.Id}\t{device.DisplayName}");
                }
            }
        });

        return cmd;
    }

    // ── oap: Open Asset Package tools ──────────────────────────────────────────

}
