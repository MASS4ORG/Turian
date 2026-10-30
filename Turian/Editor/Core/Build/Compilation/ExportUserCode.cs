namespace Turian.Editor.Core;

/// <summary>
/// Publishes the user project as a standalone executable and packages assets. A build profile chooses the target,
/// platform, configuration and script symbols. The exported folder is named after the game.
/// </summary>
public sealed class ExportUserCode(
    IAppSettings settings,
    ILogger logger,
    BuildConfiguration configuration = BuildConfiguration.Release,
    ProjectBuildProfile? profile = null,
    string? outputDirectory = null)
    : CompilerBase(settings, logger), IUserCodeCompiler
{
    /// <inheritdoc />
    protected override ProjectSettingsCollection? SettingsOverrides => profile?.Profile;

    /// <summary>The destination folder containing the executable and its content.</summary>
    internal string OutputDirectory => outputDirectory ?? ExportOutputDir(Settings.ProjectAbsoluteDir,
        Path.Combine("Output", Settings.TitleToPathFriendly ?? "Game"));

    /// <summary>Publishes the project and returns the export output directory.</summary>
    public async Task<string> ExecuteAsync()
    {
        if (BuildPlatforms.For(profile?.Profile).ValidateSettings(Settings) is { } invalid)
        {
            throw new InvalidOperationException(invalid);
        }

        var csprojFilePath = await SetupAsync(GenerateCsProjFile).ConfigureAwait(false);
        EnsureDirectory(csprojFilePath);
        if (profile?.Profile is { NativeAot: true } nativeProfile)
        {
            await ExportRuntimeGenerator.GenerateAsync(csprojFilePath, Settings, nativeProfile, Logger)
                .ConfigureAwait(false);
        }

        var exportOutputPath = OutputDirectory;
        Logger.LogInformation("Export output path: {Path}", exportOutputPath);
        EnsureDirectory(exportOutputPath + Path.DirectorySeparatorChar);
        if (RunsHeadlessByAccident(Settings.ProjectAbsoluteDir, profile?.Profile))
        {
            Logger.LogWarning(
                "No render brick is installed, so this client build runs headless. Add {Brick} to play it in a window",
                ProjectPackages.RenderBrick);
        }

        var configName = (profile?.Profile.Configuration ?? configuration) == BuildConfiguration.Release
            ? "Release"
            : "Debug";
        var platform = BuildPlatforms.For(profile?.Profile);
        var context = new BuildPlatformContext(Settings, Logger, csprojFilePath, exportOutputPath, configName,
            profile?.Profile);

        // SetupAsync already restored this project, so skip the implicit restore of `dotnet publish`.
        var startInfo = new ProcessStartInfo(DotnetLocator.Find())
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in platform.PublishArguments(context))
        {
            startInfo.ArgumentList.Add(argument);
        }

        WithoutLocatorEnvironment(startInfo);

        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            Logger.LogError("Export failed: {Output}\n{Error}", output, error);
            throw new InvalidOperationException("Export failed.");
        }

        if (profile?.Profile.NativeAot == true)
        {
            Logger.LogInformation("NativeAOT publish: {Output}\n{Error}", output, error);
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
