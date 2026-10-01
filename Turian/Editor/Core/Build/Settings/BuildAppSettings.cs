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
    [HideInEditor, JsonIgnore]
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
    /// List of external packages
    /// </summary>
    public string[] Packages => [
        "Microsoft.Extensions.Logging",
        "Silk.NET",
        "StbImageSharp",
        "System.Composition",
        "System.Text.Json",
    ];

    /// <summary>
    /// Packages the generated game project references.
    /// </summary>
    /// <remarks>
    /// The engine assemblies come in as bare <c>&lt;Reference HintPath&gt;</c> entries, not project
    /// references, so NuGet dependencies do not flow transitively: <b>every package
    /// <c>Turian.Engine.Core</c> references must be listed here</b> or the game fails at
    /// runtime the first time it reaches the code that needs it.
    /// </remarks>
    public (string, string)[] PackageReferences => [
        ("Microsoft.Extensions.Hosting", "10.0.5"),
        ("Microsoft.Extensions.Logging.Abstractions", "10.0.12"),
        ("Silk.NET.Input.Extensions", "2.23.0"),
        ("Silk.NET.Vulkan.Extensions.EXT", "2.23.0"),
        ("Silk.NET.Input", "2.23.0"),
        ("Silk.NET.Maths", "2.23.0"),
        ("Silk.NET.Vulkan", "2.23.0"),
        ("Silk.NET.Vulkan.Extensions.KHR", "2.23.0"),
        ("Silk.NET.Windowing.Common", "2.23.0"),
        ("Silk.NET.Windowing.Glfw", "2.23.0"),
        ("StbImageSharp", "2.30.16"),
        ("System.Composition", "10.0.0"),
        ("System.IO.Hashing", "10.0.12"),
    ];

    /// <summary>
    /// List of internal engine packages
    /// </summary>
    public string[] InternalPackages => [
        "MASS4/Attributes/MASS4.Attributes",
        "Gaya/Gaya.Packages/Gaya.Packages",
        "Turian/Engine/Attributes/Turian.Engine.Attributes",
        "Turian/Engine/Core/Turian.Engine.Core"
    ];

    /// <summary>
    /// The engine assemblies user code references, as (folder, assembly name) pairs.
    /// </summary>
    public (string, string)[] TurianPackages => [
        ("MASS4/Attributes", "MASS4.Attributes"),
        ("Gaya/Gaya.Packages", "Gaya.Packages"),
        ("Turian/Engine/Attributes", "Turian.Engine.Attributes"),
        ("Turian/Engine/Core", "Turian.Engine.Core")
    ];

    /// <summary>
    /// The target Framework
    /// </summary>
    public string TargetFramework => "net10.0";

    /// <summary>
    /// The target SDK
    /// </summary>
    public string TargetSdk => "Microsoft.NET.Sdk";
}
