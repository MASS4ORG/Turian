namespace Turian.Editor.Core;

/// <summary>
/// Publishes the user project as a standalone executable and packages assets. A build profile chooses the target,
/// platform, configuration and script symbols. The exported folder is named after the game. With a deployment, the
/// finished build is then installed on a device and started.
/// </summary>
public sealed class ExportUserCode(
    IAppSettings settings,
    ILogger logger,
    BuildConfiguration configuration = BuildConfiguration.Release,
    ProjectBuildProfile? profile = null,
    string? outputDirectory = null,
    BuildDeployment? deployment = null)
    : CompilerBase(settings, logger), IUserCodeCompiler
{
    readonly ProjectBuildProfile? profile = deployment?.Development == true && profile is not null
        ? profile with { Profile = profile.Profile.ForDevelopment() }
        : profile;

    /// <inheritdoc />
    protected override ProjectSettingsCollection? SettingsOverrides => profile?.Profile;

    /// <summary>Where the build was installed, after an export with a deployment; otherwise null.</summary>
    public DeployedBuild? Deployed { get; private set; }

    /// <summary>The destination folder containing the executable and its content.</summary>
    internal string OutputDirectory => outputDirectory ?? ExportOutputDir(Settings.ProjectAbsoluteDir,
        Path.Combine("Output", Settings.TitleToPathFriendly ?? "Game"));

    /// <summary>Checks effective profile settings and platform tools before expensive build work starts.</summary>
    public void Validate()
    {
        ProjectSettingsLoader.LoadFromSources(Settings, SettingsOverrides);
        var platform = BuildPlatforms.For(profile?.Profile);
        if (deployment is not null && !platform.Deploys)
        {
            throw new InvalidOperationException(
                $"{platform.DisplayName} runs on this computer. Select a device platform in Build Profiles to deploy.");
        }
        var context = Context(string.Empty);
        if (platform.ValidateExport(context) is { } invalid)
        {
            throw new InvalidOperationException(invalid);
        }

        if (platform.CheckToolchain() is { IsReady: false } status)
        {
            throw new InvalidOperationException(status.Message);
        }
    }

    /// <summary>Publishes the project and returns the export output directory.</summary>
    public async Task<string> ExecuteAsync()
    {
        Validate();
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

        var platform = BuildPlatforms.For(profile?.Profile);
        var context = Context(csprojFilePath);

        await platform.PrepareAsync(context, CancellationToken.None).ConfigureAwait(false);

        // SetupAsync already restored this project, so skip the implicit restore of `dotnet publish`.
        var startInfo = new ProcessStartInfo(DotnetLocator.Find())
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(context.ProjectFile)),
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
        if (deployment is not null)
        {
            var device = await platform.DeployAsync(context, artifact, deployment.Device, CancellationToken.None)
                .ConfigureAwait(false);
            Deployed = new DeployedBuild(platform, context, device);
        }

        return artifact.OutputDirectory;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    BuildPlatformContext Context(string projectFile) => new(Settings, Logger, projectFile, OutputDirectory,
        (profile?.Profile.Configuration ?? configuration).ToString(), profile?.Profile);

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
