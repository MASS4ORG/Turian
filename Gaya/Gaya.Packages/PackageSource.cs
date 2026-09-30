namespace Gaya.Packages;

/// <summary>Where a package comes from, read from a dependency value.</summary>
public abstract record PackageSource
{
    /// <summary>
    /// Reads a dependency value: <c>builtin:id</c>, <c>file:path</c> (a folder, or a <c>.brick</c> file), <c>git+url[#ref]</c> (a <c>./</c> or <c>../</c>
    /// url is a local repository relative to the declaring file), or else a version range to be satisfied by a source declared
    /// elsewhere.
    /// </summary>
    /// <param name="spec">The dependency value.</param>
    /// <param name="baseDirectory">The folder relative <c>file:</c> paths start from: the declaring file's.</param>
    /// <returns>The source.</returns>
    /// <exception cref="PackageException">The value is none of these.</exception>
    public static PackageSource Parse(string spec, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.StartsWith("builtin:", StringComparison.Ordinal))
            return spec.Length > "builtin:".Length
                ? new BuiltinSource(spec["builtin:".Length..])
                : throw new PackageException($"'{spec}' names no built-in package.");

        if (spec.StartsWith("file:", StringComparison.Ordinal))
        {
            var path = Path.GetFullPath(spec["file:".Length..], baseDirectory);
            return path.EndsWith(BrickArchive.Extension, StringComparison.OrdinalIgnoreCase)
                ? new ArchiveSource(path)
                : new FileSource(path);
        }

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
            : throw new PackageException($"'{spec}' is neither a builtin: id, a file: path, a git+ url nor a version range.");
    }
}

/// <summary>A package shipped with the host, read in place from its built-in folder.</summary>
/// <param name="Id">The package id.</param>
public sealed record BuiltinSource(string Id) : PackageSource
{
    /// <inheritdoc/>
    public override string ToString() => $"builtin:{Id}";
}

/// <summary>A package folder on disk, used in place and never copied to the store.</summary>
/// <param name="Path">The absolute package folder.</param>
public sealed record FileSource(string Path) : PackageSource
{
    /// <inheritdoc/>
    public override string ToString() => $"file:{Path}";
}

/// <summary>A packed <c>.brick</c> file, extracted into the store and pinned by its content hash.</summary>
/// <param name="Path">The absolute path of the <c>.brick</c> file.</param>
public sealed record ArchiveSource(string Path) : PackageSource
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
