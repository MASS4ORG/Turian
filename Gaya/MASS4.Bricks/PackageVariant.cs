namespace MASS4.Bricks;

/// <summary>
/// The variant axis of a release: a source and its bakes share one id and version, and the archive's own
/// <c>package.json</c> names which one it is. File names and release metadata never decide it.
/// </summary>
public static class PackageVariant
{
    /// <summary>The variant of a source archive, and of any archive whose manifest names none.</summary>
    public const string Source = "source";

    /// <summary>The prefix stores give bakes published before variants existed; no manifest may use it.</summary>
    public const string LegacyPrefix = "legacy-";

    /// <summary>
    /// Whether <paramref name="variant"/> is a portable target key: ASCII letters, digits and interior <c>.</c>,
    /// <c>-</c> or <c>_</c>, starting with a letter or digit, at most 100 characters.
    /// </summary>
    /// <param name="variant">The candidate key.</param>
    /// <returns>True when stores accept it in URLs and file names.</returns>
    public static bool IsValid([NotNullWhen(true)] string? variant) => variant is { Length: > 0 and <= 100 }
        && char.IsAsciiLetterOrDigit(variant[0]) && char.IsAsciiLetterOrDigit(variant[^1])
        && variant.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>The variant a bake made by <paramref name="host"/> at <paramref name="version"/> is published as.</summary>
    /// <param name="host">The host that baked it.</param>
    /// <param name="version">The host's version; bakes load only on the same major and minor.</param>
    /// <returns>The key, such as <c>turian3.3</c>.</returns>
    public static string ForHost(string host, SemanticVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(version);
        return $"{host}{version.Major}.{version.Minor}";
    }

    /// <summary>
    /// The variant an archive is stored under: its manifest's, else <see cref="Source"/>, else, for a bake from before
    /// variants, the key stores derive from its hash.
    /// </summary>
    /// <param name="manifest">The archive's manifest.</param>
    /// <param name="integrity">The archive's <c>sha256-</c> hash.</param>
    /// <returns>The variant key.</returns>
    public static string Of(PackageManifest manifest, string integrity)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Variant ?? (manifest.BakeOf is null ? Source : Legacy(integrity));
    }

    /// <summary>The key stores give a bake from before variants: the prefix and its hash in unpadded base64url.</summary>
    /// <param name="integrity">The archive's <c>sha256-</c> hash.</param>
    /// <returns>The legacy key.</returns>
    public static string Legacy(string integrity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(integrity);
        return LegacyPrefix + integrity["sha256-".Length..].TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
