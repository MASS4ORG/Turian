namespace Turian.Editor.Core;

/// <summary>Converts editor model sources to glTF 2.0 through the shared Assimp native library.</summary>
public static partial class AssimpModelConverter
{
    // Ultz.Native.Assimp ships libassimp 5 and 6 side by side, under runtimes/<rid>/native.
    // Resolving those by file name alone relies on the host's RID probing, which does not find
    // them in every environment, so the full paths are tried first and version 6 before 5.
    static readonly string[] NativeLibraryNames = BuildNativeLibraryNames();

    static string[] BuildNativeLibraryNames()
    {
        string[] fileNames = OperatingSystem.IsWindows()
            ? ["Assimp64.dll", "Assimp32.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libassimp.6.dylib", "libassimp.5.dylib"]
                : ["libassimp.so.6", "libassimp.so.5"];

        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        List<string> directories =
        [
            AppContext.BaseDirectory,
            Path.Combine(runtimes, RuntimeInformation.RuntimeIdentifier, "native"),
            Path.Combine(runtimes, PortableRuntimeIdentifier(), "native")
        ];

        List<string> candidates =
        [
            .. from directory in directories.Distinct()
            from fileName in fileNames
            let path = Path.Combine(directory, fileName)
            where File.Exists(path)
            select path,

            .. fileNames
        ];

        // Anything installed system-wide, by name, as a last resort.
        return [.. candidates];
    }

    static string PortableRuntimeIdentifier()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return PortableRuntimeIdentifier(platform, RuntimeInformation.ProcessArchitecture);
    }

    internal static string PortableRuntimeIdentifier(string platform, Architecture processArchitecture)
    {
        var architecture = processArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => processArchitecture.ToString().ToUpperInvariant(),
        };

        return $"{platform}-{architecture}";
    }

    const uint postProcessFlags = (uint)(
        PostProcessSteps.Triangulate
        | PostProcessSteps.CalculateTangentSpace
        | PostProcessSteps.GenerateSmoothNormals
        | PostProcessSteps.JoinIdenticalVertices
        | PostProcessSteps.GenerateBoundingBoxes);

    static readonly Lazy<AssimpApi> Assimp =
        new(() => new AssimpApi(AssimpApi.CreateDefaultContext(NativeLibraryNames)), isThreadSafe: true);

    internal static AssimpApi Api => Assimp.Value;
    internal static uint PostProcessFlags => postProcessFlags;

    /// <summary>Exports a triangulated glTF 2.0 binary model while preserving the source coordinate basis.</summary>
    /// <param name="sourcePath">The model source supported by Assimp.</param>
    /// <param name="destinationPath">The GLB file to write.</param>
    public static unsafe void ConvertToGlb(string sourcePath, string destinationPath)
    {
        var api = Api;
        var scene = api.ImportFile(sourcePath, postProcessFlags);
        if (scene is null)
            throw new InvalidDataException($"Assimp failed to read '{sourcePath}': {api.GetErrorStringS()}");
        try
        {
            ExportScene(api, scene, sourcePath, destinationPath);
            RepairExportedTangents(destinationPath, scene);
        }
        finally { api.ReleaseImport(scene); }
    }

    static unsafe void ExportScene(AssimpApi api, AssimpScene* scene, string sourcePath, string destinationPath)
    {
        AssimpScene* copy = null;
        api.CopyScene(scene, &copy);
        if (copy is null)
            throw new InvalidDataException($"Assimp failed to copy '{sourcePath}' for export.");
        try
        {
            // The native exporter reapplies required indexing to copied scenes without imported processing flags.
            if (api.ExportScene(copy, "glb2", destinationPath, 0) != Silk.NET.Assimp.Return.Success)
                throw new InvalidDataException($"Assimp failed to export '{sourcePath}': {api.GetErrorStringS()}");
        }
        finally { api.FreeScene(copy); }
    }
}
