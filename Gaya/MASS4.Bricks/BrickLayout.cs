namespace MASS4.Bricks;

/// <summary>
/// Where a brick keeps what hosts never import. Brick format v2 puts it all under one reserved root, <c>Brick~/</c>,
/// with the manifest's <see cref="PackageManifest.Roles"/> naming each part; earlier bricks use one tilde folder per
/// part, such as <c>Precast~</c>.
/// </summary>
public static class BrickLayout
{
    /// <summary>The reserved root of a v2 brick: shipped whole, never imported or compiled by a host.</summary>
    public const string ReservedRoot = "Brick~";

    /// <summary>C# projects the engine solution builds, never the game.</summary>
    public const string Source = "source";

    /// <summary>Test projects.</summary>
    public const string Tests = "tests";

    /// <summary>Prebuilt managed assemblies and their NuGet record.</summary>
    public const string Precast = "precast";

    /// <summary>Import results a bake twin ships, so projects mount them instead of importing.</summary>
    public const string Bake = "bake";

    /// <summary>Content offered for copying into a project.</summary>
    public const string Samples = "samples";

    /// <summary>Human documentation.</summary>
    public const string Docs = "docs";

    static readonly Dictionary<string, (string V1, string V2)> Defaults = new(StringComparer.Ordinal)
    {
        [Source] = ("Source~", $"{ReservedRoot}/Source"),
        [Tests] = ("Tests~", $"{ReservedRoot}/Tests"),
        [Precast] = ("Precast~", $"{ReservedRoot}/Precast"),
        [Bake] = ("Bake~", $"{ReservedRoot}/Bake"),
        [Samples] = ("Samples~", $"{ReservedRoot}/Samples"),
        [Docs] = ("Documentation~", $"{ReservedRoot}/Documentation"),
    };

    /// <summary>Whether a manifest uses the v2 layout, which declaring any role means.</summary>
    /// <param name="manifest">The brick's manifest.</param>
    /// <returns>True for a v2 brick.</returns>
    public static bool IsV2(PackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Roles.Count > 0;
    }

    /// <summary>
    /// A role's folder, relative to the brick folder: the first path the manifest names; for a v2 brick that names
    /// none, the default under <see cref="ReservedRoot"/> where tools write it; else the v1 tilde folder.
    /// </summary>
    /// <param name="manifest">The brick's manifest.</param>
    /// <param name="role">One of the well-known roles.</param>
    /// <returns>The relative path, with <c>/</c> separators.</returns>
    public static string RelativePathOf(PackageManifest manifest, string role)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.RolePaths(role) is [var first, ..])
        {
            return first;
        }

        var (v1, v2) = Defaults[role];
        return IsV2(manifest) ? v2 : v1;
    }

    /// <summary>A role's folder inside a brick folder.</summary>
    /// <param name="packageRoot">The brick folder.</param>
    /// <param name="manifest">The brick's manifest.</param>
    /// <param name="role">One of the well-known roles.</param>
    /// <returns>The absolute path; it may not exist.</returns>
    public static string PathOf(string packageRoot, PackageManifest manifest, string role) =>
        Path.Combine(packageRoot, RelativePathOf(manifest, role).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>All declared folders for a role, in declaration order; absent v2 roles have no payload.</summary>
    /// <param name="packageRoot">The brick folder.</param>
    /// <param name="manifest">The brick manifest.</param>
    /// <param name="role">The role to read.</param>
    /// <returns>Absolute role paths, or the legacy folder for a v1 manifest.</returns>
    public static IReadOnlyList<string> PathsOf(string packageRoot, PackageManifest manifest, string role)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var paths = manifest.RolePaths(role);
        return IsV2(manifest)
            ? [.. paths.Select(path => Path.Combine(packageRoot, path.Replace('/', Path.DirectorySeparatorChar)))]
            : [PathOf(packageRoot, manifest, role)];
    }

    /// <summary>Whether a role path lies inside <see cref="ReservedRoot"/>, as v2 requires.</summary>
    /// <param name="path">The path from the manifest.</param>
    /// <returns>True for a relative <c>Brick~/…</c> path without <c>..</c> or backslashes.</returns>
    public static bool IsInsideReservedRoot(string path) =>
        path.StartsWith(ReservedRoot + "/", StringComparison.Ordinal) && path.Length > ReservedRoot.Length + 1
        && !path.Contains('\\', StringComparison.Ordinal) && path.Split('/').All(static s => s is not ("" or "." or ".."));
}
