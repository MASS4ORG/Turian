namespace Turian.Editor.Core;

/// <summary>Where imported Unity assets go.</summary>
/// <param name="Folder">The absolute folder the files are written into.</param>
/// <param name="MetaRoot">The folder asset paths in metas are relative to: the project or the brick.</param>
public sealed record UnityImportTarget(string Folder, string MetaRoot);

/// <summary>
/// Imports a <c>.unitypackage</c> as Turian assets. Textures, models, audio and data files are copied with their Unity
/// guids as asset ids, so anything that points at them by guid still does. Materials, prefabs and scenes are converted
/// as far as they map. Scripts, shaders, animation and other Unity-only assets are reported and left out.
/// </summary>
public static class UnityPackageImporter
{
    static readonly string[] TextureExtensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".psd", ".gif", ".hdr"];

    static readonly (string[] Extensions, string Reason)[] Skipped =
    [
        ([".cs"], "C# written against UnityEngine; port the script to Turian's Component API"),
        ([".shader", ".cginc", ".hlsl", ".compute", ".shadergraph", ".shadersubgraph"], "a Unity shader; Turian materials are PBR"),
        ([".asmdef", ".asmref"], "an assembly definition for Unity C# code"),
        ([".anim", ".controller", ".overrideController", ".mask", ".playable", ".signal"], "Unity animation; Turian has no equivalent yet"),
        ([".asset", ".lighting", ".cubemap", ".flare", ".physicMaterial", ".physicsMaterial2D", ".terrainlayer", ".brush"], "a Unity serialized object with no Turian equivalent"),
        ([".dll", ".so", ".dylib", ".a", ".aar", ".jar", ".bundle"], "a native or managed plugin"),
    ];

    /// <summary>The licensing reminder to show with an import: the tool moves files, it does not grant rights.</summary>
    public const string LicenseNotice =
        "The imported assets keep the license they were published under. Check the terms of the package you imported "
        + "(for an Asset Store package, the Unity Asset Store EULA) before using them outside Unity.";

    /// <summary>Imports a package into a folder.</summary>
    /// <param name="packagePath">The <c>.unitypackage</c> file.</param>
    /// <param name="target">Where the assets go.</param>
    /// <returns>What was imported and what was left out.</returns>
    /// <exception cref="InvalidDataException">The file is not a Unity package.</exception>
    public static UnityImportReport Import(string packagePath, UnityImportTarget target)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"turian-unity-{Guid.NewGuid():N}");
        try
        {
            var assets = UnityPackageReader.Read(packagePath, scratch);
            var report = new UnityImportReport();
            var importers = Importers();
            var context = new UnityImportContext(target, report, assets.ToDictionary(static a => a.Guid), importers);

            // Everything other assets can point at goes first, so a material finds the texture it names.
            foreach (var asset in assets.Where(static a => a.AssetFile is not null)
                         .OrderBy(static a => Kind(a.Pathname)).ThenBy(static a => a.Pathname, StringComparer.Ordinal))
                ImportAsset(asset, context);

            if (report.Converted.Count > 0 || report.Skipped.Count > 0)
                report.Notes.Add("Unity and Turian both use +Y up and +Z forward, so positions and rotations were copied as they are.");
            return report;
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    static int Kind(string pathname) => Path.GetExtension(pathname).ToLowerInvariant() switch
    {
        ".mat" => 1,
        ".prefab" => 2,
        ".unity" => 3,
        _ => 0,
    };

    static void ImportAsset(UnityPackageAsset asset, UnityImportContext context)
    {
        var extension = Path.GetExtension(asset.Pathname);
        var relative = RelativeTarget(asset.Pathname);

        if (Array.Find(Skipped, s => s.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) is { Reason: not null } skip)
        {
            context.Report.Skipped.Add(new UnityImportEntry(asset.Pathname, null, skip.Reason));
            return;
        }

        try
        {
            switch (extension.ToLowerInvariant())
            {
                case ".mat":
                    UnityMaterialConverter.Convert(asset, relative, context);
                    break;
                case ".prefab":
                case ".unity":
                    UnityPrefabConverter.Convert(asset, relative, context);
                    break;
                default:
                    CopyRaw(asset, relative, context);
                    break;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException or InvalidOperationException)
        {
            context.Report.Skipped.Add(new UnityImportEntry(asset.Pathname, null, $"could not be converted: {ex.Message}"));
        }
    }

    static void CopyRaw(UnityPackageAsset asset, string relative, UnityImportContext context)
    {
        var target = context.Place(relative);
        File.Copy(asset.AssetFile!, target, overwrite: true);

        var importer = context.ImporterFor(target);
        var meta = importer.CreateAsset(target);
        meta.Id = asset.Guid;
        meta.RelativePath = context.MetaPath(target);
        var notes = new List<string>();

        if (meta is TextureAsset texture && asset.MetaText is not null) ApplyTextureSettings(texture, asset.MetaText, notes);
        if (asset.MetaText is not null) ApplyLabels(meta, asset.MetaText);

        context.WriteMeta(target, meta);
        context.Report.Converted.Add(new UnityImportEntry(asset.Pathname, context.Relative(target), notes.Count == 0 ? null : string.Join("; ", notes)));
    }

    /// <summary>The import settings that map: color space, mip maps, size limit and normal maps.</summary>
    static void ApplyTextureSettings(TextureAsset texture, string metaText, List<string> notes)
    {
        var importer = UnityYaml.Parse(metaText)["TextureImporter"];
        if (importer is null) return;

        if (importer["sRGBTexture"] is { } srgb) texture.IsSrgb = srgb.Long(1) != 0;
        if (importer["mipmaps"]?["enableMipMap"] is { } mips) texture.GenerateMips = mips.Long(1) != 0;

        // Unity's default limit is 2048, which is not a choice somebody made.
        if (importer["maxTextureSize"] is { } size && size.Long(2048) is var limit and not 2048)
            texture.ImportSettings.MaxResolution = (int)limit;

        // textureType 1 is a normal map, which is not color data.
        if (importer["textureType"]?.Long() == 1)
        {
            texture.IsSrgb = false;
            notes.Add("normal map: check the green channel convention");
        }
    }

    static void ApplyLabels(Asset meta, string metaText)
    {
        if (UnityYaml.Parse(metaText)["labels"] is { } labels) meta.Labels.AddRange(labels.Items.Select(static l => l.Text).Where(static l => l.Length > 0));
    }

    /// <summary>Unity's <c>Assets/…</c> path without its first segment, which is the project's own folder.</summary>
    static string RelativeTarget(string pathname)
    {
        var parts = pathname.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts.Length > 1 && parts[0] == "Assets" ? parts[1..] : parts);
    }

    static List<IAssetImporter> Importers() =>
    [
        .. typeof(IAssetImporter).Assembly.GetTypes()
            .Where(static t => typeof(IAssetImporter).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false }
                               && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(static t => t.GetCustomAttribute<DefaultOptionAttribute>() is not null)
            .ThenBy(static t => t == typeof(GenericAssetImporter))
            .Select(static t => (IAssetImporter)Activator.CreateInstance(t)!),
    ];
}

/// <summary>What converters share while one package is imported.</summary>
sealed class UnityImportContext(UnityImportTarget target, UnityImportReport report,
    IReadOnlyDictionary<Guid, UnityPackageAsset> assets, IReadOnlyList<IAssetImporter> importers)
{
    public UnityImportReport Report { get; } = report;

    public IReadOnlyDictionary<Guid, UnityPackageAsset> Assets { get; } = assets;

    public IAssetImporter ImporterFor(string path) => importers.First(i => i.IsValid(path));

    /// <summary>The absolute path a relative asset path is written to, with its folder created.</summary>
    public string Place(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(target.Folder, relative));
        if (!path.StartsWith(Path.GetFullPath(target.Folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"{relative} would be written outside the destination.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public string MetaPath(string path) => Path.GetRelativePath(target.MetaRoot, path).Replace('\\', '/');

    public string Relative(string path) => Path.GetRelativePath(target.Folder, path).Replace('\\', '/');

    public void WriteMeta(string assetPath, Asset meta) =>
        File.WriteAllText($"{assetPath}.meta", AssetImporter.SerializeAssetMetadata(meta));

    /// <summary>Whether a guid names an asset of the package (Unity's built-in resources do not).</summary>
    public bool Has(Guid guid) => Assets.ContainsKey(guid);
}
