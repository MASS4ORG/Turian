namespace Turian.Editor.Core;

/// <summary>A third-party assembly copied flat into a brick's <c>Precast~/lib</c> by a NuGet restore.</summary>
/// <param name="PackageId">The package the assembly was restored from.</param>
/// <param name="Version">The package's unified version.</param>
/// <param name="FileName">The assembly file name.</param>
public sealed record BrickRuntimeFile(string PackageId, string Version, string FileName);

/// <summary>
/// Writes the <c>.deps.json</c> an <see cref="AssemblyDependencyResolver"/> reads beside a
/// brick's prebuilt assembly. Entries are <c>project</c> libraries with flat file names, because the restore copies
/// every unified package assembly next to the brick assembly instead of keeping package folder layouts.
/// </summary>
public static class BrickDepsJson
{
    const string TargetName = ".NETCoreApp,Version=v10.0";

    /// <summary>Writes <c>&lt;assemblyPath&gt;.deps.json</c> listing the brick assembly and <paramref name="files"/>.</summary>
    /// <param name="assemblyPath">The brick's prebuilt assembly.</param>
    /// <param name="files">The restored third-party assemblies beside it.</param>
    public static void Write(string assemblyPath, IReadOnlyList<BrickRuntimeFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(files);

        var name = Path.GetFileNameWithoutExtension(assemblyPath);
        var version = ReadVersion(assemblyPath);
        var targets = new JsonObject
        {
            [$"{name}/{version}"] = new JsonObject
            {
                ["runtime"] = new JsonObject { [$"{name}.dll"] = new JsonObject() },
            },
        };
        var libraries = new JsonObject
        {
            [$"{name}/{version}"] = new JsonObject
            {
                ["type"] = "project",
                ["serviceable"] = false,
                ["sha512"] = string.Empty,
            },
        };

        foreach (var file in files.OrderBy(static f => f.FileName, StringComparer.Ordinal))
        {
            targets[$"{file.PackageId}/{file.Version}"] = new JsonObject
            {
                ["runtime"] = new JsonObject { [file.FileName] = new JsonObject() },
            };
            libraries[$"{file.PackageId}/{file.Version}"] = new JsonObject
            {
                ["type"] = "project",
                ["serviceable"] = false,
                ["sha512"] = string.Empty,
            };
        }

        var document = new JsonObject
        {
            ["runtimeTarget"] = new JsonObject { ["name"] = TargetName, ["signature"] = string.Empty },
            ["targets"] = new JsonObject { [TargetName] = targets },
            ["libraries"] = libraries,
        };
        File.WriteAllText(Path.ChangeExtension(assemblyPath, ".deps.json"),
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    static string ReadVersion(string assemblyPath)
    {
        try
        {
            return AssemblyName.GetAssemblyName(assemblyPath).Version?.ToString() ?? "0.0.0";
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException)
        {
            return "0.0.0";
        }
    }
}
