namespace Turian.Editor.Core;

/// <summary>Creates <see cref="DataAssetVariants"/> files: the way to tweak a data asset, such as one of a brick's, without copying or forking it.</summary>
public static class DataAssetVariantFactory
{
    /// <summary>Writes a variant of the data asset in <paramref name="baseFile"/>, with its meta.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="baseFile">The base data asset's file, which must have a <c>.meta</c> beside it.</param>
    /// <param name="folder">The folder, relative to the project, the variant is written into.</param>
    /// <param name="name">The variant's file name without extension.</param>
    /// <returns>The variant's file and its asset id.</returns>
    /// <exception cref="FileNotFoundException">The base or its meta does not exist.</exception>
    /// <exception cref="InvalidOperationException">The base is not a data asset, or the variant file exists.</exception>
    public static (string Path, Guid Id) Create(string projectRoot, string baseFile, string folder, string name)
    {
        var baseMeta = $"{baseFile}.meta";
        if (!File.Exists(baseFile) || !File.Exists(baseMeta))
            throw new FileNotFoundException($"{baseFile} and its .meta must both exist.");
        if (!GenericAssetImporter.IsDataAssetPath(baseFile))
            throw new InvalidOperationException($"{baseFile} is not a data asset.");

        var baseId = Guid.Parse((string)JsonNode.Parse(File.ReadAllText(baseMeta))!["Id"]!);
        var directory = Path.Combine(projectRoot, folder);
        var path = Path.Combine(directory, $"{name}.dataasset");
        if (File.Exists(path)) throw new InvalidOperationException($"{path} already exists.");

        var id = Guid.NewGuid();
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, DataAssetVariants.Create(baseId, File.ReadAllText(baseFile), id));
        Serializer.Save($"{path}.meta", new DataAssetAsset
        {
            Id = id,
            RelativePath = Path.GetRelativePath(projectRoot, path).Replace('\\', '/'),
        });
        return (path, id);
    }
}
