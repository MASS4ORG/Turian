using MASS4.Bricks;

namespace Turian.Editor.Core;

/// <summary>Stages installed bricks' native runtime payloads without knowledge of their feature or backend.</summary>
static class BrickNativeAssets
{
    internal static void AddToProject(ProjectRootElement project, IReadOnlyList<ResolvedPackage> bricks,
        string? runtimeIdentifier = null, bool publish = true)
    {
        var rid = string.IsNullOrWhiteSpace(runtimeIdentifier)
            ? RuntimeInformation.RuntimeIdentifier : runtimeIdentifier.Trim();
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var brick in bricks)
        {
            var roots = BrickAssemblies.PrecastDirectories(brick)
                .Select(static root => Path.Combine(root, "lib")).Prepend(brick.RootPath);
            foreach (var root in roots)
            {
                var directory = Path.Combine(root, "runtimes", rid, "native");
                if (!Directory.Exists(directory)) { continue; }
                foreach (var file in Directory.EnumerateFiles(directory)
                             .Where(static path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                             .Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(file);
                    if (staged.TryGetValue(name, out var previous))
                    {
                        if (!File.ReadAllBytes(previous).AsSpan().SequenceEqual(File.ReadAllBytes(file)))
                        {
                            throw new InvalidOperationException($"Bricks provide different native files named '{name}'.");
                        }
                        continue;
                    }
                    staged.Add(name, file);
                    if (AndroidAbi(rid) is { } abi)
                    {
                        project.AddItemGroup().AddItem("AndroidNativeLibrary", file).AddMetadata("Abi", abi, true);
                        continue;
                    }
                    var content = project.AddItemGroup().AddItem("Content", file);
                    content.AddMetadata("Link", name, true);
                    content.AddMetadata("CopyToOutputDirectory", "PreserveNewest", true);
                    content.AddMetadata("CopyToPublishDirectory", publish ? "PreserveNewest" : "Never", true);
                    content.AddMetadata("ExcludeFromSingleFile", "true", true);
                }
            }
        }
    }

    static string? AndroidAbi(string runtimeIdentifier) => runtimeIdentifier switch
    {
        "android-arm64" => "arm64-v8a",
        "android-x64" => "x86_64",
        _ => null,
    };
}
