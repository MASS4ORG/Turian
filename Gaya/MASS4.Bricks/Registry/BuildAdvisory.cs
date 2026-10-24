namespace MASS4.Bricks;

/// <summary>
/// A curator's record about a toolchain, served at <c>api/v1/build-advisories</c>: builds it matches carry its message,
/// and are refused when <see cref="Reject"/> is set. Toolchain age alone never refuses a build.
/// </summary>
/// <param name="Id">The record's id, which rejections link to.</param>
/// <param name="PackagePrefix">The brick name prefix it covers, matched on whole dot-separated segments.</param>
/// <param name="Image">The exact digest-pinned image it matches; any image when null.</param>
/// <param name="TurianVersionRange">The build host versions it matches; any version when null.</param>
/// <param name="Message">The curator's explanation.</param>
/// <param name="Reject">Whether matching builds are refused rather than warned about.</param>
public sealed record BuildAdvisory(Guid Id, string PackagePrefix, string? Image, string? TurianVersionRange, string Message,
    bool Reject)
{
    /// <summary>The path, relative to a store's origin, the record is served at.</summary>
    public string Path => $"api/v1/build-advisories/{Id}";

    /// <summary>
    /// Whether the record covers a build of <paramref name="packageId"/>: the prefix, and every selector it states. A
    /// selector the build cannot answer, such as an image when only the version is known, does not match.
    /// </summary>
    /// <param name="packageId">The brick id.</param>
    /// <param name="image">The image that built it, when known.</param>
    /// <param name="turianVersion">The build host version that built it, when known.</param>
    /// <returns>True when the record applies.</returns>
    public bool Matches(string packageId, string? image, string? turianVersion)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        return Covers(packageId) && (Image is not null || TurianVersionRange is not null)
               && (Image is null || Image == image) && MatchesVersion(turianVersion);
    }

    /// <summary>Whether the prefix covers <paramref name="packageId"/> on whole dot-separated segments.</summary>
    bool Covers(string packageId) =>
        packageId == PackagePrefix || packageId.StartsWith(PackagePrefix + ".", StringComparison.Ordinal);

    /// <summary>Whether the version range, when stated, holds for <paramref name="turianVersion"/>.</summary>
    bool MatchesVersion(string? turianVersion) => TurianVersionRange is null
        || (VersionRange.TryParse(TurianVersionRange, out var range)
            && SemanticVersion.TryParse(turianVersion, out var version) && range.IsSatisfiedBy(version));

    /// <summary>The first record that refuses the build, else the ones that warn about it.</summary>
    /// <param name="advisories">The store's records.</param>
    /// <param name="packageId">The brick id.</param>
    /// <param name="builds">Every image and version known to have built it, such as the submitted and the recorded one.</param>
    /// <returns>The refusing record, or null, and the warning records.</returns>
    public static (BuildAdvisory? Rejected, IReadOnlyList<BuildAdvisory> Warnings) Evaluate(
        IEnumerable<BuildAdvisory> advisories, string packageId, IEnumerable<(string? Image, string? TurianVersion)> builds)
    {
        ArgumentNullException.ThrowIfNull(advisories);
        var known = builds.ToList();
        var matching = advisories.Where(a => known.Any(b => a.Matches(packageId, b.Image, b.TurianVersion)))
            .OrderBy(static a => a.Id).ToList();
        return (matching.FirstOrDefault(static a => a.Reject), [.. matching.Where(static a => !a.Reject)]);
    }
}
