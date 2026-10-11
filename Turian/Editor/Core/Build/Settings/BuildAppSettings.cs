namespace Turian.Editor.Core;

/// <inheritdoc cref="IAppSettings"/>
[TypeId("04636c74-0b91-5822-afc0-126ffdc58e8a")]
public class BuildAppSettings : IdObject, IBuildAppSettings
{
    BricksSettings? bricks;

    /// <inheritdoc/>
    public string ProjectAbsoluteDir { get; set; } = string.Empty;

    /// <inheritdoc/>
    public string AssetsAbsoluteDir => Path.Combine(ProjectAbsoluteDir, "Assets");

    /// <inheritdoc/>
    public string CacheAbsoluteDir => Path.Combine(ProjectAbsoluteDir, ".Cache");

    /// <inheritdoc/>
    /// <remarks>
    /// Computed as the path from the generated <c>.csproj</c> directory
    /// (<c>.Cache/Source/</c>) to the project's unified <c>Assets/</c> folder,
    /// so that the MSBuild glob resolves to all <c>*.cs</c> files under <c>Assets/</c>.
    /// </remarks>
    public string CacheSourceRelativeDir => Path.GetRelativePath(Path.Combine(CacheAbsoluteDir, SourceSubDir), AssetsAbsoluteDir);

    /// <inheritdoc/>
    /// <remarks>
    /// This refers to the sub-directory inside <see cref="CacheAbsoluteDir"/> where the
    /// generated <c>.csproj</c> file is placed (<c>.Cache/Source/</c>).
    /// It does <b>not</b> refer to a user-facing source folder; user code now lives
    /// alongside other assets in the <c>Assets/</c> folder.
    /// </remarks>
    public string SourceSubDir => "Source";

    /// <inheritdoc/>
    public string ExportSourceSubDir => "Export";

    /// <inheritdoc/>
    public string? Title { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public ProjectSettingsSet Loaded { get; } = new();

    /// <inheritdoc/>
    [Hide, JsonIgnore]
    public BricksSettings Bricks => BricksSettings.ForProject(ref bricks, ProjectAbsoluteDir);

    /// <inheritdoc/>
    public T Get<T>()
        where T : ProjectSettingsAsset, new() => Loaded.Get<T>();

    /// <inheritdoc/>
    public string? TitleToPathFriendly => Title?.SanitizeFilename(' ');

    /// <inheritdoc/>
    public IAppSettings Load(IAppSettings appSettings)
    {
        ArgumentNullException.ThrowIfNull(appSettings);
        if (ReferenceEquals(appSettings, this)) return this;

        ProjectAbsoluteDir = appSettings.ProjectAbsoluteDir;
        Title = appSettings.Title;
        Loaded.Clear();
        Loaded.UseAll(appSettings.Loaded);
        return this;
    }

    /// <summary>
    /// Names of the runtime packages resolved for the game launcher.
    /// </summary>
    public string[] Packages => [.. RuntimeDependencies.Current.Packages.Select(static package => package.Item1)];

    /// <summary>
    /// Exact NuGet packages and versions resolved for the engine and game launcher by this editor's build.
    /// </summary>
    /// <remarks>
    /// Generated projects reference engine DLLs directly, so this build-generated manifest supplies their NuGet
    /// dependencies without requiring the editor's source checkout or shipping its tooling dependencies.
    /// </remarks>
    public (string, string)[] PackageReferences => [.. RuntimeDependencies.Current.Packages];

    /// <summary>
    /// Runtime project paths relative to the checkout root, excluding file extensions.
    /// </summary>
    public string[] InternalPackages => [.. RuntimeDependencies.Current.Projects];

    /// <summary>
    /// Runtime assemblies as (source folder, assembly name) pairs, derived from the launcher reference graph.
    /// </summary>
    public (string, string)[] TurianPackages => [.. RuntimeDependencies.Current.Assemblies];

    /// <summary>
    /// The target Framework
    /// </summary>
    public string TargetFramework => RuntimeDependencies.Current.TargetFramework;

    /// <summary>
    /// The target SDK
    /// </summary>
    public string TargetSdk => "Microsoft.NET.Sdk";
}
