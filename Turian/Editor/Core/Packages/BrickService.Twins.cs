using MASS4.Bricks;

namespace Turian.Editor.Core;

/// <summary>Bake twins: packing a brick with its twin, and choosing, per project, which of the two a brick resolves to.</summary>
public static partial class BrickService
{
    /// <summary>Packs a brick folder, and its bake twin when <paramref name="twin"/> is set.</summary>
    /// <param name="packageRoot">The brick folder.</param>
    /// <param name="outputDirectory">Where the files and their hashes are written.</param>
    /// <param name="precast">What the folder's precast payload was built with.</param>
    /// <param name="twin">Whether to pack the twin too, as <see cref="PackWithTwin"/> does.</param>
    /// <returns>The brick, then its twin.</returns>
    /// <exception cref="PackageException">The brick cannot be packed.</exception>
    public static IReadOnlyList<BrickPackResult> PackFiles(string packageRoot, string outputDirectory, PackagePrecast? precast,
        bool twin)
    {
        if (!twin)
        {
            return [Pack(packageRoot, outputDirectory, precast)];
        }

        var (source, baked) = PackWithTwin(packageRoot, outputDirectory, precast);
        return [source, baked];
    }

    /// <summary>
    /// Packs a brick folder as a source brick and its bake twin. The source file leaves out the precast payload and the
    /// bake; the twin, <c>id.bake</c>, carries them with the assets, without the C# sources or scripts, and records the
    /// source file it was baked from.
    /// </summary>
    /// <param name="packageRoot">The brick folder, with its precast payload built when it has code, and baked.</param>
    /// <param name="outputDirectory">Where both files and their hashes are written.</param>
    /// <param name="precast">What the payload was built with, from <see cref="PrecastAsync"/>.</param>
    /// <returns>The source file and the twin.</returns>
    /// <exception cref="PackageException">The brick fails verification, refuses twins, or has nothing to bake.</exception>
    public static (BrickPackResult Source, BrickPackResult Twin) PackWithTwin(string packageRoot, string outputDirectory,
        PackagePrecast? precast = null)
    {
        CheckPackable(packageRoot);
        string[] prefixes = ["gaya", ProjectPackages.HostName];
        var manifest = PackageManifest.Load(packageRoot, prefixes);
        if (!manifest.AllowsTwins)
        {
            throw new PackageException($"{packageRoot} sets bake to '{PackageManifest.BakeNone}': it is its own consumable form and has no twin.");
        }

        var payloads = BrickLayout.PathsOf(packageRoot, manifest, BrickLayout.Precast);
        var bakes = BrickLayout.PathsOf(packageRoot, manifest, BrickLayout.Bake);
        CheckTwinPayload(packageRoot, manifest, payloads, bakes);

        var source = BrickArchive.Pack(packageRoot, outputDirectory, prefixes,
            exclude: [.. payloads.Concat(bakes).Select(path => Path.GetRelativePath(packageRoot, path).Replace('\\', '/'))]);
        string[] sources = [.. SourcePaths(manifest), "*.cs", "*.cs.meta"];
        var twin = BrickArchive.Pack(packageRoot, outputDirectory, prefixes, precast, sources,
            new PackageBakeOf { Brick = source.Manifest.Name, Version = source.Manifest.Version, Integrity = source.Integrity });
        return (source, twin);
    }

    static void CheckTwinPayload(string packageRoot, PackageManifest manifest, IReadOnlyList<string> payloads,
        IReadOnlyList<string> bakes)
    {
        var compiled = payloads.Where(Directory.Exists)
            .Any(static path => Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories).Any());
        if (!compiled && HasCode(packageRoot, manifest))
        {
            throw new PackageException($"{packageRoot} has code but no precast assemblies to bake; build them, or pack with --precast.");
        }

        if (!compiled && !bakes.Any(Directory.Exists))
        {
            throw new PackageException($"{packageRoot} has neither code nor baked assets for a twin; run brick bake first.");
        }

    }

    /// <summary>The folders holding C# projects the engine solution builds: the <c>source</c> role, or v1 <c>Source~</c>.</summary>
    static IReadOnlyList<string> SourcePaths(PackageManifest manifest) =>
        BrickLayout.IsV2(manifest) ? manifest.RolePaths(BrickLayout.Source) : [BrickLayout.RelativePathOf(manifest, BrickLayout.Source)];

    /// <summary>Whether a brick folder holds C#: projects in its source folders, or scripts the project would compile.</summary>
    static bool HasCode(string packageRoot, PackageManifest manifest) =>
        SourcePaths(manifest).Any(path => Directory.Exists(Path.Combine(packageRoot, path)))
        || Directory.EnumerateFiles(packageRoot, "*.cs", SearchOption.AllDirectories)
            .Any(file => !AssetDatabase.IsInTildeFolder(file, packageRoot));

    /// <summary>
    /// Takes a registry brick as its source or as its bake twin from now on, and resolves. With twins on, a source is
    /// kept by listing it in the manifest's <c>sources</c>; with them off, the declaration itself moves between the
    /// source's id and the twin's.
    /// </summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The source brick's id, or its twin's.</param>
    /// <param name="toSource">True for the source, false for the twin.</param>
    /// <returns>The resolution.</returns>
    /// <exception cref="PackageException">The brick cannot be swapped, or does not resolve afterwards.</exception>
    public static PackageResolution Swap(string projectRoot, string id, bool toSource)
    {
        var source = id.EndsWith(BrickArchive.TwinSuffix, StringComparison.Ordinal) ? id[..^BrickArchive.TwinSuffix.Length] : id;
        var twin = BrickArchive.TwinIdOf(source);
        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.DirectoryName, ProjectManifest.FileName);
        var before = File.ReadAllText(manifestPath);
        var selected = List(projectRoot).FirstOrDefault(p => p.Id == source || p.Manifest.BakeOf?.Brick == source)
                       ?? throw new PackageException($"The project does not use {source}.");
        var version = selected.Manifest.BakeOf?.Version ?? selected.Version;
        var (from, to) = toSource ? (twin, source) : (source, twin);
        var embedded = Path.Combine(projectRoot, ProjectManifest.DirectoryName, source);
        string? trash = null;
        var created = false;

        // With twins on, a declared source already resolves to its twin; only a declared twin is renamed.
        if ((!manifest.Twins || from == twin) && manifest.Dependencies.Remove(from, out var spec))
        {
            manifest.Dependencies[to] = spec;
        }
        else if (!manifest.Twins)
        {
            throw new PackageException(toSource
                ? $"The project does not declare {twin}."
                : $"The project does not declare {source}; turn twins on to take the twins of the bricks it only depends on.");
        }

        manifest.Dependencies[manifest.Twins ? source : to] = version.ToString();
        if (!toSource)
        {
            _ = manifest.Sources.Remove(source);
        }
        else if (manifest.Twins && !manifest.Sources.Contains(source))
        {
            manifest.Sources.Add(source);
        }

        if (!toSource && File.Exists(Path.Combine(embedded, PackageManifest.FileName)))
        {
            trash = ProjectBricks.Revert(projectRoot, source, ["gaya", ProjectPackages.HostName]);
        }

        try
        {
            var resolution = SaveManifest(projectRoot, manifest);
            if (toSource && resolution.Packages.FirstOrDefault(p => p.Id == source) is { Origin: PackageOrigin.Registry } original)
            {
                _ = ProjectBricks.Embed(original, projectRoot, ["gaya", ProjectPackages.HostName]);
                created = true;
                ProjectPackages.Invalidate(projectRoot);
                return ProjectPackages.Resolve(projectRoot);
            }

            return resolution;
        }
        catch
        {
            if (created)
            {
                Directory.Delete(embedded, recursive: true);
            }

            if (trash is not null)
            {
                Directory.Move(trash, embedded);
            }

            File.WriteAllText(manifestPath, before);
            ProjectPackages.Invalidate(projectRoot);
            _ = ProjectPackages.Resolve(projectRoot);
            throw;
        }
    }

    /// <summary>Turns taking bake twins of registry bricks on or off, and resolves.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="on">Whether to take twins.</param>
    /// <returns>The resolution.</returns>
    /// <exception cref="PackageException">The bricks do not resolve afterwards.</exception>
    public static PackageResolution SetTwins(string projectRoot, bool on)
    {
        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        manifest.Twins = on;
        return SaveManifest(projectRoot, manifest);
    }
}
