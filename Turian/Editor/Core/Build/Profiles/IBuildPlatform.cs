namespace Turian.Editor.Core;

/// <summary>A device a platform can deploy and run a game on, as the device picker lists it.</summary>
/// <param name="Id">The platform's identifier for the device, such as an adb serial.</param>
/// <param name="DisplayName">The name shown to people choosing a device.</param>
public sealed record BuildDevice(string Id, string DisplayName);

/// <summary>Whether a platform's build toolchain is ready, and what is missing when it is not.</summary>
/// <param name="IsReady">True when the platform can export with no extra setup.</param>
/// <param name="Message">A short human-readable status.</param>
public sealed record BuildPlatformStatus(bool IsReady, string Message);

/// <summary>What a platform's finalize step produced.</summary>
/// <param name="OutputDirectory">The folder holding the exported game.</param>
public sealed record BuildArtifact(string OutputDirectory);

/// <summary>What a platform needs to configure, publish or finalize a generated runtime project.</summary>
/// <param name="Settings">The project settings.</param>
/// <param name="Logger">The logger.</param>
/// <param name="ProjectFile">The generated runtime project.</param>
/// <param name="OutputDirectory">Where the published game goes.</param>
/// <param name="Configuration">The MSBuild configuration, <c>Release</c> or <c>Debug</c>.</param>
/// <param name="Profile">The build profile followed, or <c>null</c> for a default client build.</param>
/// <param name="Mode">Whether the project is generated for play mode or export.</param>
public sealed record BuildPlatformContext(
    IBuildAppSettings Settings,
    ILogger Logger,
    string ProjectFile,
    string OutputDirectory,
    string Configuration,
    BuildProfile? Profile,
    ExecutableGenerationMode Mode = ExecutableGenerationMode.Export);

/// <summary>
/// A platform games export to, the Unity build target counterpart: what it can run a game as, the launcher sources its
/// runtime project compiles, how that project is configured and published, and how the published output is finished.
/// Desktop ships with the editor; other platforms, such as mobile, consoles or XR, come from editor bricks declaring
/// <see cref="BuildPlatformAttribute"/>.
/// </summary>
public interface IBuildPlatform
{
    /// <summary>The id build profiles name the platform by, such as <c>desktop</c>.</summary>
    string Id { get; }

    /// <summary>The name shown to people choosing a platform.</summary>
    string DisplayName { get; }

    /// <summary>What the platform can run a game as.</summary>
    IReadOnlyList<BuildTarget> Targets { get; }

    /// <summary>The folder of launcher sources copied into the generated runtime project.</summary>
    string LauncherDirectory { get; }

    /// <summary>The target framework the platform's exported project builds for, such as <c>net10.0</c>.</summary>
    string TargetFramework { get; }

    /// <summary>The <see cref="TextureBuildTarget"/> key whose artifacts the platform ships.</summary>
    string TextureTarget { get; }

    /// <summary>The runtime identifiers the platform offers in the profile's RID picker.</summary>
    IReadOnlyList<string> RuntimeIdentifiers { get; }

    /// <summary>The runtime dependency graph the platform's exported game references.</summary>
    RuntimeDependencies Dependencies { get; }

    /// <summary>
    /// The devices the platform can deploy to, or <c>null</c> when it runs games on this machine and needs no picker.
    /// </summary>
    IReadOnlyList<BuildDevice>? Devices { get; }

    /// <summary>Adds the platform-specific properties and items of the generated runtime project.</summary>
    /// <param name="project">The generated project, not yet saved.</param>
    /// <param name="context">The project, settings, mode and profile of the build.</param>
    void ConfigureProject(ProjectRootElement project, BuildPlatformContext context);

    /// <summary>The <c>dotnet</c> arguments that publish the generated runtime project.</summary>
    /// <param name="context">The project, output, configuration and profile of the build.</param>
    /// <returns>The arguments, without the <c>dotnet</c> command itself.</returns>
    IReadOnlyList<string> PublishArguments(BuildPlatformContext context);

    /// <summary>Reports whether the platform's toolchain is installed and ready.</summary>
    /// <returns>The toolchain status.</returns>
    BuildPlatformStatus CheckToolchain();

    /// <summary>Checks the project's settings against what the platform requires, before an export starts.</summary>
    /// <param name="settings">The project settings.</param>
    /// <returns>What the user must fix, or <c>null</c> when the settings are acceptable.</returns>
    string? ValidateSettings(IAppSettings settings) => null;

    /// <summary>Finishes a published build: strips artifacts, names the executable, and places content.</summary>
    /// <param name="context">The published project, output, configuration and profile.</param>
    /// <param name="cancellationToken">A token to cancel the finalize step.</param>
    /// <returns>The finished artifact.</returns>
    Task<BuildArtifact> FinalizeAsync(BuildPlatformContext context, CancellationToken cancellationToken);
}

/// <summary>Identifies an <see cref="IBuildPlatform"/> an editor brick provides.</summary>
/// <param name="platformType">A type with a public parameterless constructor implementing the platform.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class BuildPlatformAttribute(Type platformType) : Attribute
{
    /// <summary>The platform type.</summary>
    public Type PlatformType { get; } = platformType;
}

/// <summary>The build platforms this editor can export to.</summary>
public static class BuildPlatforms
{
    /// <summary>The id of the built-in desktop platform: Windows, macOS and Linux on any architecture.</summary>
    public const string Desktop = "desktop";

    /// <summary>The built-in desktop platform, then those of the loaded editor bricks.</summary>
    /// <returns>The platforms.</returns>
    public static IReadOnlyList<IBuildPlatform> All() =>
    [
        new DesktopBuildPlatform(),
        .. AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic)
            .SelectMany(static assembly => assembly.GetCustomAttributes<BuildPlatformAttribute>())
            .Select(static attribute => (IBuildPlatform)Activator.CreateInstance(attribute.PlatformType)!),
    ];

    /// <summary>The platform with id <paramref name="id"/>, ignoring case; the desktop platform for none.</summary>
    /// <param name="id">A platform id, or <c>null</c>.</param>
    /// <returns>The platform.</returns>
    /// <exception cref="InvalidOperationException">No platform has that id; the message lists the installed ones.</exception>
    public static IBuildPlatform Find(string? id)
    {
        var platforms = All();
        if (string.IsNullOrWhiteSpace(id)) return platforms[0];

        return platforms.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"No build platform '{id}' is installed. Platforms: {string.Join(", ", platforms.Select(static p => p.Id))}.");
    }

    /// <summary>The platform <paramref name="profile"/> builds for, checked to support its target.</summary>
    /// <param name="profile">The build profile, or <c>null</c> for a default client build.</param>
    /// <returns>The platform.</returns>
    /// <exception cref="InvalidOperationException">The platform is missing or cannot build the profile's target.</exception>
    public static IBuildPlatform For(BuildProfile? profile)
    {
        var platform = Find(profile?.Platform);
        var target = profile?.Target ?? BuildTarget.Client;
        return platform.Targets.Contains(target)
            ? platform
            : throw new InvalidOperationException($"The {platform.DisplayName} platform cannot build a {target}.");
    }

    /// <summary>The runtime identifiers the desktop platform publishes for, in picker order.</summary>
    internal static readonly string[] DesktopRuntimeIdentifiers =
        ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
}

/// <summary>
/// Windows, macOS and Linux on any architecture: a self-contained single-file executable published by
/// <c>dotnet publish</c>, for the profile's runtime identifier or this machine's.
/// </summary>
sealed class DesktopBuildPlatform : IBuildPlatform
{
    /// <inheritdoc />
    public string Id => BuildPlatforms.Desktop;

    /// <inheritdoc />
    public string DisplayName => "Desktop";

    /// <inheritdoc />
    public IReadOnlyList<BuildTarget> Targets { get; } = [BuildTarget.Client, BuildTarget.Server];

    /// <inheritdoc />
    public string TargetFramework => Dependencies.TargetFramework;

    /// <inheritdoc />
    public string TextureTarget => TextureBuildTarget.Pc;

    /// <inheritdoc />
    public IReadOnlyList<string> RuntimeIdentifiers { get; } = BuildPlatforms.DesktopRuntimeIdentifiers;

    /// <inheritdoc />
    public RuntimeDependencies Dependencies => RuntimeDependencies.Current;

    /// <inheritdoc />
    public IReadOnlyList<BuildDevice>? Devices => null;

    /// <inheritdoc />
    public string LauncherDirectory
    {
        get
        {
            // A published editor ships the launchers under lib/; a source build beside its own assemblies.
            var published = Path.Combine(AppContext.BaseDirectory, "lib", "Platform", "Desktop");
            return Directory.Exists(published)
                ? published
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Platform", "Desktop"));
        }
    }

    /// <inheritdoc />
    public void ConfigureProject(ProjectRootElement project, BuildPlatformContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        // A server keeps its console on Windows, for its log and Ctrl+C; a game's window has none.
        var console = context.Profile?.Target == BuildTarget.Server;
        var group = project.AddPropertyGroup();
        group.AddProperty("OutputType", console ? "Exe" : "WinExe");
        group.AddProperty("TargetFramework", TargetFramework);
        group.AddProperty("ImplicitUsings", "disable");
        group.AddProperty("Nullable", "enable");
        group.AddProperty("AllowUnsafeBlocks", "true");
        // The launcher reads DEVELOPMENT_BUILD from a build profile's symbols.
        group.AddProperty("DefineConstants", "$(DefineConstants);$(TurianDefines)");

        var icon = Path.Combine(context.Settings.CacheAbsoluteDir, ProjectIcon.IcoFileName);
        if (File.Exists(icon)) group.AddProperty("ApplicationIcon", icon);

        if (context.Mode == ExecutableGenerationMode.Export)
        {
            group.AddProperty("DebugType", "None");
            group.AddProperty("DebugSymbols", "false");
            group.AddProperty("PublishSingleFile", "true");
            group.AddProperty("SelfContained", "true");
            // In the project rather than on the command line, so restore resolves the same runtime packs.
            if (!string.IsNullOrWhiteSpace(context.Profile?.RuntimeIdentifier))
                group.AddProperty("RuntimeIdentifier", context.Profile!.RuntimeIdentifier!.Trim());
        }
        else
        {
            group.AddProperty("DebugType", "portable");
            group.AddProperty("DebugSymbols", "true");
            group.AddProperty("PublishSingleFile", "false");
            group.AddProperty("SelfContained", "false");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> PublishArguments(BuildPlatformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<string> arguments =
            ["publish", context.ProjectFile, "-o", context.OutputDirectory, "-c", context.Configuration, "--no-restore"];
        // The profile's script symbols reach every script project the runtime project references, even none, so
        // they replace the active profile's that the script projects default to.
        if (context.Profile is { } profile)
            arguments.Add($"-p:TurianDefines={string.Join("%3B", profile.Symbols)}");
        return arguments;
    }

    /// <inheritdoc />
    public BuildPlatformStatus CheckToolchain() =>
        new(true, "Desktop builds need only the .NET SDK this editor runs on.");

    /// <inheritdoc />
    public Task<BuildArtifact> FinalizeAsync(BuildPlatformContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        BuildFinalizer.CleanArtifacts(context);
        BuildFinalizer.RenameExecutable(context, ExecutableExtension(context.Profile?.RuntimeIdentifier));
        BuildFinalizer.CopyProjectSettings(context);
        BuildFinalizer.WriteTypeManifest(context);
        BuildFinalizer.PackageAssets(context);

        return Task.FromResult(new BuildArtifact(context.OutputDirectory));
    }

    /// <summary>The executable's extension for the target runtime: <c>.exe</c> on Windows, none elsewhere.</summary>
    /// <param name="runtimeIdentifier">The build's runtime identifier; empty for this machine's.</param>
    internal static string ExecutableExtension(string? runtimeIdentifier) =>
        (string.IsNullOrWhiteSpace(runtimeIdentifier)
            ? OperatingSystem.IsWindows()
            : runtimeIdentifier.Trim().StartsWith("win", StringComparison.OrdinalIgnoreCase))
            ? ".exe"
            : string.Empty;
}
