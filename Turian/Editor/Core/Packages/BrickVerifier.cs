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
