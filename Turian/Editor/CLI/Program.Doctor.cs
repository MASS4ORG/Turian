using MASS4.Bricks;

namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command DoctorCommand()
    {
        var platformArg = new Argument<string?>("platform")
        {
            Description = "Platform to check; all installed platforms when omitted",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var projectOption = new Option<DirectoryInfo?>("--project")
        {
            Description = "Project whose platform bricks to check; the current project when omitted",
        };
        var command = new Command("doctor", "Check installed platform toolchains without exporting a project")
        {
            platformArg, projectOption,
        };
        command.SetAction(result =>
        {
            try
            {
                var platforms = DoctorPlatforms(result.GetValue(projectOption));
                return ReportToolchains(platforms, result.GetValue(platformArg), Console.Out);
            }
            catch (Exception error) when (error is PackageException or IOException or InvalidOperationException
                                          or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        });
        return command;
    }

    static IReadOnlyList<IBuildPlatform> DoctorPlatforms(DirectoryInfo? project)
    {
        var candidate = project?.FullName ?? Environment.CurrentDirectory;
        var directory = SettingsService.ResolveProjectDirectory(candidate);
        if (project is not null && directory is null)
        {
            throw new DirectoryNotFoundException($"{candidate} is not a project folder.");
        }

        if (directory is not null)
        {
            var packages = ProjectPackages.Resolve(directory, locked: true).Packages;
            BrickAssemblies.Load(packages, Log.Logger, directory);
        }

        LoadUserSettings();
        return BuildPlatforms.All();
    }

    static int ReportToolchains(IReadOnlyList<IBuildPlatform> platforms, string? id, TextWriter output)
    {
        var selected = string.IsNullOrWhiteSpace(id)
            ? platforms
            : platforms.Where(platform => string.Equals(platform.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (selected.Count == 0)
        {
            output.WriteLine($"No build platform '{id ?? "(any)"}' is installed. "
                             + $"Installed: {string.Join(", ", platforms.Select(static platform => platform.Id))}.");
            output.WriteLine("Install the platform brick and run brick restore --project <project>, "
                             + "then doctor --project <project>.");
            return 1;
        }

        var exitCode = 0;
        foreach (var platform in selected.OrderBy(static platform => platform.Id, StringComparer.Ordinal))
        {
            exitCode |= ReportToolchain(platform, output);
        }

        return exitCode;
    }

    static int ReportToolchain(IBuildPlatform platform, TextWriter output)
    {
        try
        {
            var status = platform.CheckToolchain();
            output.WriteLine($"{platform.Id}: {(status.IsReady ? "READY" : "MISSING")} - {status.Message}");
            return status.IsReady ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            output.WriteLine($"{platform.Id}: ERROR - {error.Message}");
            return 1;
        }
    }

}
