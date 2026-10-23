using MASS4.Bricks;
using NuGet.Versioning;

namespace Turian.Editor.Core;

/// <summary>
/// Helpers for a brick's NuGet package dependencies: version unification across bricks and floating-version detection.
/// </summary>
public static class BrickNuGet
{
    /// <summary>Whether <paramref name="version"/> is a floating version, such as <c>2.5.*</c> or <c>6.*</c>.</summary>
    /// <param name="version">The version string from a brick's <c>nuget</c> map.</param>
    /// <returns>True when the version contains a wildcard.</returns>
    public static bool IsFloatingVersion(string version) => version.IndexOf('*') >= 0;

    /// <summary>
    /// Parses <paramref name="version"/> as a NuGet version, using NuGet's SemVer 2.0 ordering, including prerelease labels.
    /// </summary>
    /// <param name="version">The version string.</param>
    /// <returns>The parsed version.</returns>
    /// <exception cref="FormatException">The version is not a valid NuGet version.</exception>
    public static NuGetVersion ParseVersion(string version) => NuGetVersion.Parse(version);

    /// <summary>
    /// Unifies the NuGet maps of the given bricks for <paramref name="platform"/>: within a major version the highest
    /// requested version wins; differing major versions are a hard error naming both bricks and both versions.
    /// </summary>
    /// <param name="bricks">The bricks whose maps to unify.</param>
    /// <param name="platform">The build platform.</param>
    /// <returns>Package id -> unified version.</returns>
    /// <exception cref="InvalidOperationException">A floating version is present or two bricks request different major versions.</exception>
    public static IReadOnlyDictionary<string, string> UnifyVersions(IEnumerable<ResolvedPackage> bricks, string platform = "desktop")
    {
        ArgumentNullException.ThrowIfNull(bricks);
        var requests = new Dictionary<string, List<(string Brick, string Version)>>();
        foreach (var brick in bricks)
        {
            foreach (var (package, version) in brick.Manifest.NugetFor(platform))
            {
                if (IsFloatingVersion(version))
                {
                    throw new InvalidOperationException(
                        $"Brick {brick.Id} requests floating NuGet version '{version}' for {package}; pin an exact version.");
                }

                if (!requests.TryGetValue(package, out var list))
                {
                    list = [];
                    requests[package] = list;
                }

                list.Add((brick.Id, version));
            }
        }

        var unified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (package, list) in requests)
        {
            var highest = list[0].Version;
            var highestBrick = list[0].Brick;
            foreach (var request in list.Skip(1))
            {
                var current = ParseVersion(highest);
                var candidate = ParseVersion(request.Version);
                if (current.Major != candidate.Major)
                {
                    throw new InvalidOperationException(
                        $"NuGet version conflict for {package}: brick {highestBrick} requests {highest} "
                        + $"and brick {request.Brick} requests {request.Version}.");
                }

                if (candidate > current)
                {
                    highest = request.Version;
                    highestBrick = request.Brick;
                }
            }

            unified[package] = highest;
        }

        return unified;
    }
}
