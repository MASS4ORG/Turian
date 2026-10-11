namespace Turian.Editor.Core;

/// <summary>What a platform needs to publish a generated runtime project.</summary>
/// <param name="ProjectFile">The generated runtime project.</param>
/// <param name="OutputDirectory">Where the published game goes.</param>
/// <param name="Configuration">The MSBuild configuration, <c>Release</c> or <c>Debug</c>.</param>
/// <param name="Profile">The build profile followed, or <c>null</c> for a default client build.</param>
public sealed record BuildPlatformContext(string ProjectFile, string OutputDirectory, string Configuration,
    BuildProfile? Profile);

/// <summary>
/// A platform games export to, the Unity build target counterpart: what it can run a game as, the launcher sources its
/// runtime project compiles, and how that project is published. Desktop ships with the editor; other platforms, such
/// as mobile, consoles or XR, come from editor bricks declaring <see cref="BuildPlatformAttribute"/>.
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

    /// <summary>The <c>dotnet</c> arguments that publish the generated runtime project.</summary>
    /// <param name="context">The project, output, configuration and profile of the build.</param>
    /// <returns>The arguments, without the <c>dotnet</c> command itself.</returns>
    IReadOnlyList<string> PublishArguments(BuildPlatformContext context);
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
    public IReadOnlyList<string> PublishArguments(BuildPlatformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<string> arguments =
            ["publish", context.ProjectFile, "-o", context.OutputDirectory, "-c", context.Configuration, "--no-restore"];
        // The profile's script symbols reach every script project the runtime project references.
        if (context.Profile?.Symbols is { Count: > 0 } symbols)
            arguments.Add($"-p:TurianDefines={string.Join("%3B", symbols)}");
        return arguments;
    }
}
