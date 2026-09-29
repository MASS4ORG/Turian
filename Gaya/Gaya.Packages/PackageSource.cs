namespace Gaya.Packages;

/// <summary>Where a package comes from, read from a dependency value.</summary>
public abstract record PackageSource
{
    /// <summary>
    /// Reads a dependency value: <c>file:path</c>, <c>git+url[#ref]</c> (a <c>./</c> or <c>../</c> url is a local
    /// repository relative to the declaring file), or else a version range to be satisfied by a source declared
    /// elsewhere.
    /// </summary>
    /// <param name="spec">The dependency value.</param>
    /// <param name="baseDirectory">The folder relative <c>file:</c> paths start from: the declaring file's.</param>
    /// <returns>The source.</returns>
    /// <exception cref="PackageException">The value is none of these.</exception>
    public static PackageSource Parse(string spec, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.StartsWith("file:", StringComparison.Ordinal))
            return new FileSource(Path.GetFullPath(spec["file:".Length..], baseDirectory));

        if (spec.StartsWith("git+", StringComparison.Ordinal))
        {
            var url = spec["git+".Length..];
            var hash = url.LastIndexOf('#');
            var (repository, reference) = hash < 0 ? (url, null) : (url[..hash], url[(hash + 1)..]);

            // A relative local repository is found from the declaring file, like a file: path.
            if (repository.StartsWith("./", StringComparison.Ordinal) || repository.StartsWith("../", StringComparison.Ordinal))
                repository = Path.GetFullPath(repository, baseDirectory);

            return new GitSource(repository, reference);
        }

        return VersionRange.TryParse(spec, out var range)
            ? new RangeSource(range)
            : throw new PackageException($"'{spec}' is neither a file: path, a git+ url nor a version range.");
    }
}

/// <summary>A package folder on disk, used in place and never copied to the store.</summary>
/// <param name="Path">The absolute package folder.</param>
public sealed record FileSource(string Path) : PackageSource
{
    /// <inheritdoc/>
    public override string ToString() => $"file:{Path}";
}

/// <summary>A package at the root of a git repository.</summary>
/// <param name="Url">The repository url, in any form <c>git clone</c> accepts.</param>
/// <param name="Ref">The tag, branch or commit; the default branch when null.</param>
public sealed record GitSource(string Url, string? Ref) : PackageSource
{
    /// <inheritdoc/>
    public override string ToString() => Ref is null ? $"git+{Url}" : $"git+{Url}#{Ref}";
}

/// <summary>A version range another declaration or a registry must satisfy.</summary>
/// <param name="Range">The acceptable versions.</param>
public sealed record RangeSource(VersionRange Range) : PackageSource
{
    /// <inheritdoc/>
    public override string ToString() => Range.ToString();
}
