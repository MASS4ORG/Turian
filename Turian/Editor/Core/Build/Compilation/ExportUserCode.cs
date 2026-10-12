namespace Turian.Editor.Core;

/// <summary>
/// Publishes the user project as a standalone executable and packages assets. A build profile chooses the target,
/// platform, configuration and script symbols, and exports into <c>Output/&lt;profile&gt;</c>.
/// </summary>
public sealed class ExportUserCode(
    IAppSettings settings,
    ILogger logger,
    BuildConfiguration configuration = BuildConfiguration.Release,
    ProjectBuildProfile? profile = null)
    : CompilerBase(settings, logger), IUserCodeCompiler
{
    /// <inheritdoc />
    protected override ProjectSettingsCollection? SettingsOverrides => profile?.Profile;

    /// <summary>Publishes the project and returns the export output directory.</summary>
    public async Task<string> ExecuteAsync()
    {
        var csprojFilePath = await SetupAsync(GenerateCsProjFile).ConfigureAwait(false);
        EnsureDirectory(csprojFilePath);

        var exportOutputPath = ExportOutputDir(Settings.ProjectAbsoluteDir,
            profile is null ? "Output" : Path.Combine("Output", profile.Name.SanitizeFilename(' ')));
        Logger.LogInformation("Export output path: {Path}", exportOutputPath);
        EnsureDirectory(exportOutputPath + Path.DirectorySeparatorChar);
        if (RunsHeadlessByAccident(Settings.ProjectAbsoluteDir, profile?.Profile))
            Logger.LogWarning(
                "No render brick is installed, so this client build runs headless. Add {Brick} to play it in a window",
                ProjectPackages.RenderBrick);

        var configName = (profile?.Profile.Configuration ?? configuration) == BuildConfiguration.Release
            ? "Release"
            : "Debug";
        var platform = BuildPlatforms.For(profile?.Profile);
        var context = new BuildPlatformContext(Settings, Logger, csprojFilePath, exportOutputPath, configName,
            profile?.Profile);

        // SetupAsync already restored this project, so skip the implicit restore of `dotnet publish`.
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in platform.PublishArguments(context))
            startInfo.ArgumentList.Add(argument);
        WithoutLocatorEnvironment(startInfo);

        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Logger.LogError("Export failed: {Output}", output);
            throw new InvalidOperationException("Export failed.");
        }

        var artifact = await platform.FinalizeAsync(context, CancellationToken.None).ConfigureAwait(false);

        Logger.LogInformation("Export succeeded");
        return artifact.OutputDirectory;
    }

    // ── Private helpers ────────────────────────────────────────────────────────


    string GenerateCsProjFile()
    {
        var project = CsProjectGenerator.GenerateExecutable(Settings, Logger, ExecutableGenerationMode.Export,
            profile?.Profile);
        GeneratedProject.Save(project);
        return project.FullPath;
    }

    /// <summary>A client build without a render brick opens no window: the game runs the headless player.</summary>
    internal static bool RunsHeadlessByAccident(string projectRoot, BuildProfile? profile) =>
        (profile?.Target ?? BuildTarget.Client) == BuildTarget.Client
        && !BuildProfiles.HasPlayer(BuildProfiles.ShippingBricks(ProjectPackages.ResolveOrEmpty(projectRoot),
            BuildTarget.Client));
}
