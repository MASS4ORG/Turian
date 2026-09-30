using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>
/// Makes a stub of a brick: the same asset ids and type ids with placeholder content. Teams that build on a brick
/// another team is still making pin the stub; <see cref="BrickVerifier.VerifyAgainst"/> checks it stays in step.
/// </summary>
public static class BrickStub
{
    static readonly string[] KeptExtensions = [".meta", ".json", ".dataasset", ".asset", ".prefab", ".ui", ".uss", ".mat", ".txt", ".md"];

    /// <summary>Writes a stub of the brick at <paramref name="packageRoot"/> into <paramref name="outputDirectory"/>.</summary>
    /// <param name="packageRoot">The real brick's folder.</param>
    /// <param name="outputDirectory">The stub's folder; it must not exist or be empty.</param>
    /// <returns>The stub's version, <c>&lt;version&gt;-stub</c>.</returns>
    /// <remarks>
    /// Text assets, assembly definitions and metas are kept. Binary assets become placeholders (a one-pixel image for
    /// PNG, an empty file otherwise). Each script becomes an empty class that derives from <see cref="Component"/> under the
    /// same name and type id, enough for scenes and prefabs to load; code using a class's members needs the real brick.
    /// </remarks>
    /// <exception cref="PackageException">The output folder is not empty, or the brick is invalid.</exception>
    public static string Write(string packageRoot, string outputDirectory)
    {
        packageRoot = Path.GetFullPath(packageRoot);
        var manifest = PackageManifest.Load(packageRoot, ["gaya", ProjectPackages.HostName]);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new PackageException($"{outputDirectory} is not empty.");

        var scripts = UserCodeTypeManifestGenerator.ScriptTypes(packageRoot, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
            .ToDictionary(static t => t.FullyQualifiedName, static t => t);
        foreach (var file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
            var segments = relative.Split('/');
            if (segments.Any(static s => s is ".git" or "bin" or "obj" or "Precast~" or "Source~") || relative.EndsWith(".brick", StringComparison.Ordinal)
                || relative == PackageManifest.FileName) continue;

            var target = Path.Combine(outputDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (relative.EndsWith(".cs", StringComparison.Ordinal)) WriteScriptStub(file, target, scripts);
            else if (KeptExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) File.Copy(file, target);
            else WritePlaceholder(file, target);
        }

        manifest.Version = SemanticVersion.Parse($"{manifest.Version!.Major}.{manifest.Version.Minor}.{manifest.Version.Patch}-stub");
        manifest.Upstream = null;
        manifest.Precast = null;
        manifest.Save(outputDirectory);
        return manifest.Version.ToString();
    }

    static void WriteScriptStub(string source, string target, Dictionary<string, UserCodeTypeEntry> scripts)
    {
        var type = scripts.Values.FirstOrDefault(t => SameScript(source, t));
        if (type is null)
        {
            File.WriteAllText(target, "// Placeholder: this script declares no class the brick exposes.\n");
            return;
        }

        var dot = type.FullyQualifiedName.LastIndexOf('.');
        var (ns, name) = dot < 0 ? (null, type.FullyQualifiedName) : (type.FullyQualifiedName[..dot], type.FullyQualifiedName[(dot + 1)..]);
        var declaration = $"public class {name} : Turian.Engine.Core.Component\n{{\n}}\n";
        File.WriteAllText(target, ns is null ? declaration : $"namespace {ns};\n\n{declaration}");
    }

    static bool SameScript(string script, UserCodeTypeEntry entry)
    {
        var meta = $"{script}.meta";
        return File.Exists(meta) && JsonNode.Parse(File.ReadAllText(meta)) is JsonObject json
               && Guid.TryParse((string?)json["Id"], out var id) && id == entry.TypeId;
    }

    static void WritePlaceholder(string source, string target)
    {
        if (Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            using var bitmap = new SKBitmap(1, 1);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(target, png.ToArray());
        }
        else
        {
            File.WriteAllBytes(target, []);
        }
    }
}
