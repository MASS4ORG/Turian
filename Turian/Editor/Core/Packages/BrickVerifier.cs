using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>
/// Checks a brick folder before it is packed or published: its manifest, and that every asset has a <c>.meta</c>
/// with a unique id, since those ids are what consumers reference.
/// </summary>
public static class BrickVerifier
{
    static readonly string[] UnimportedNames = ["package.json", ".gitignore", ".gitattributes"];
    static readonly string[] UnimportedPrefixes = ["README", "LICENSE", "CHANGELOG", "NOTICE"];

    /// <summary>Finds what is wrong with the brick at <paramref name="packageRoot"/>.</summary>
    /// <param name="packageRoot">The brick folder.</param>
    /// <returns>One line per problem; empty when the brick is sound.</returns>
    public static IReadOnlyList<string> Verify(string packageRoot)
    {
        packageRoot = Path.GetFullPath(packageRoot);
        var issues = new List<string>();

        try
        {
            _ = PackageManifest.Load(packageRoot, ["gaya", ProjectPackages.HostName]);
        }
        catch (PackageException ex)
        {
            issues.Add(ex.Message);
            return issues;
        }

        var seen = new Dictionary<Guid, string>();
        foreach (var file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
            if (IsUnimported(relative)) continue;

            if (relative.EndsWith(".meta", StringComparison.Ordinal))
            {
                CheckMeta(file, relative, seen, issues);
                if (!File.Exists(file[..^".meta".Length]))
                    issues.Add($"{relative} has no asset beside it.");
            }
            else if (!File.Exists($"{file}.meta"))
            {
                issues.Add($"{relative} has no .meta; assets must ship theirs so their ids stay stable.");
            }
        }

        return issues;
    }

    /// <summary>
    /// <see cref="Verify"/> plus a check that the brick exposes everything <paramref name="reference"/> does: every
    /// asset id with the same kind, and every type id with the same class name. For a stub, against the real brick.
    /// </summary>
    /// <param name="packageRoot">The brick folder to check, such as a stub.</param>
    /// <param name="reference">The brick to cover: a folder, or a <c>.brick</c> file.</param>
    /// <returns>One line per problem; empty when the brick covers the reference.</returns>
    public static IReadOnlyList<string> VerifyAgainst(string packageRoot, string reference)
    {
        var issues = Verify(packageRoot).ToList();
        var temporary = (string?)null;
        try
        {
            var referenceRoot = Path.GetFullPath(reference);
            if (File.Exists(referenceRoot))
            {
                temporary = Path.Combine(Path.GetTempPath(), $"turian-verify-{Guid.NewGuid():N}");
                BrickArchive.Extract(referenceRoot, temporary);
                referenceRoot = temporary;
            }

            issues.AddRange(BrickContract.Read(packageRoot).Lacks(BrickContract.Read(referenceRoot)));
            return issues;
        }
        finally
        {
            if (temporary is not null) Directory.Delete(temporary, recursive: true);
        }
    }

    static void CheckMeta(string file, string relative, Dictionary<Guid, string> seen, List<string> issues)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("Id", out var id) || !Guid.TryParse(id.GetString(), out var guid)
                || guid == Guid.Empty)
            {
                issues.Add($"{relative} has no valid Id.");
            }
            else if (!seen.TryAdd(guid, relative))
            {
                issues.Add($"{relative} reuses the id {guid} of {seen[guid]}.");
            }
        }
        catch (JsonException)
        {
            issues.Add($"{relative} is not valid JSON.");
        }
    }

    static bool IsUnimported(string relative)
    {
        var segments = relative.Split('/');
        var name = segments[^1];
        return segments[..^1].Any(static s => s.EndsWith('~') || s == ".git")
               || UnimportedNames.Contains(name)
               || UnimportedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
