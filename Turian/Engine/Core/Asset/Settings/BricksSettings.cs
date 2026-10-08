using Gaya.Packages;

namespace Turian.Engine.Core;

/// <summary>The project's brick manifest, available before asset loading and edited with other project settings.</summary>
public sealed class BricksSettings : ProjectManifest
{
    readonly string projectFolder;

    /// <summary>Reads the project's declared dependencies without applying machine-specific overrides.</summary>
    public BricksSettings(string projectFolder)
    {
        this.projectFolder = projectFolder;
        Reload();
    }

    /// <summary>Reuses the loaded settings until the owning application's project folder changes.</summary>
    public static BricksSettings ForProject(ref BricksSettings? current, string projectFolder) =>
        current is not null && current.projectFolder == projectFolder
            ? current
            : current = new BricksSettings(projectFolder);

    /// <summary>Reads the manifest again after a package operation or an external edit.</summary>
    public void Reload()
    {
        var manifest = string.IsNullOrWhiteSpace(projectFolder)
            ? new ProjectManifest()
            : Load(projectFolder, includeUserOverride: false).Manifest;
        Dependencies = manifest.Dependencies;
        ScopedRegistries = manifest.ScopedRegistries;
    }

    /// <summary>Writes the declared dependencies and registries to the project's shared manifest.</summary>
    public void Save()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        Save(projectFolder);
    }
}
