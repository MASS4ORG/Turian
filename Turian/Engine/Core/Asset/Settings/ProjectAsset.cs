namespace Turian.Engine.Core;

/// <summary>
/// A list of settings assets, at most one per kind: a project's complete set, or a build profile's overrides of it.
/// An override is usually a variant of the project's asset of the same kind.
/// </summary>
public abstract class ProjectSettingsCollection : DataAsset
{
    /// <summary>The registered settings assets; a kind listed twice uses its first entry.</summary>
    public List<DataAssetReference<ProjectSettingsAsset>> Settings { get; set; } = [];
}

/// <summary>
/// The project's entry point, read from the fixed path <c>Assets/Project.dataasset</c>: the settings assets in
/// force, the build profiles and the active one, and the engine version that last saved the project.
/// </summary>
[TypeId("3a5514c7-c592-41f7-8d1f-33cf663aafc1")]
public class ProjectAsset : ProjectSettingsCollection
{
    /// <summary>The asset's file name, directly under <c>Assets</c>.</summary>
    public const string FileName = "Project.dataasset";

    /// <summary>The engine version that last saved the project, which decides whether it needs upgrading.</summary>
    public string? EngineVersion { get; set; }

    /// <summary>The build profiles of the project.</summary>
    public List<DataAssetReference<ProjectSettingsCollection>> BuildProfiles { get; set; } = [];

    /// <summary>
    /// The profile Build &amp; Run and exports without a named profile follow; none builds the plain project.
    /// </summary>
    public DataAssetReference<ProjectSettingsCollection>? ActiveBuildProfile { get; set; }

    /// <summary>The path of the project asset in a project's <c>Assets</c> folder.</summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <returns>The absolute path, whether or not the file exists.</returns>
    public static string PathIn(string assetsDirectory) => Path.Combine(assetsDirectory, FileName);

    /// <summary>Reads the project asset of a project.</summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <returns>The asset, or <c>null</c> when the project has none or it cannot be read.</returns>
    public static ProjectAsset? Load(string assetsDirectory)
    {
        var path = PathIn(assetsDirectory);
        if (!File.Exists(path)) return null;

        try
        {
            return DataAsset.LoadContent(path, database: null) as ProjectAsset;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Logger.LogWarning(ex, "The project asset {Path} could not be read", path);
            return null;
        }
    }
}
