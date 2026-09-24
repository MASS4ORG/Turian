namespace Turian.Editor.Core;

/// <inheritdoc cref="IAppSettings"/>
public interface IBuildAppSettings : IAppSettings
{
    /// <summary>
    /// Get the absolute path of the Cache folder
    /// </summary>
    string CacheAbsoluteDir { get; }

    /// <summary>
    /// Gets the relative path from the generated <c>.csproj</c> location inside the cache to the
    /// project's <c>Assets</c> folder. Used as the source glob root for user code compilation.
    /// </summary>
    string CacheSourceRelativeDir { get; }

    /// <summary>
    /// Gets the sub-directory name used to organise the generated <c>.csproj</c> inside the cache
    /// folder (e.g. <c>"Source"</c>). This is an internal cache layout detail and does <em>not</em>
    /// refer to the user-facing folder where code resides — user code now lives in <c>Assets/</c>.
    /// </summary>
    string SourceSubDir { get; }

    /// <summary>
    /// Get the folder that export source files are present
    /// </summary>
    string ExportSourceSubDir { get; }

    /// <summary>
    /// List of external packages
    /// </summary>
    string[] Packages { get; }

    /// <summary>
    /// List of external packages
    /// </summary>
    (string, string)[] PackageReferences { get; }

    /// <summary>
    /// List of internal engine packages
    /// </summary>
    string[] InternalPackages { get; }

    /// <summary>
    /// List of internal engine packages
    /// </summary>
    (string, string)[] TurianPackages { get; }

    /// <summary>
    /// The target Framework
    /// </summary>
    string TargetFramework { get; }

    /// <summary>
    /// The target SDK
    /// </summary>
    string TargetSdk { get; }
}
