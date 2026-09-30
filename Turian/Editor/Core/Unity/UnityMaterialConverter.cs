namespace Turian.Editor.Core;

/// <summary>
/// Converts a Unity <c>.mat</c> into a Turian <c>.material</c>, reading the property names the Standard, URP Lit and HDRP
/// Lit shaders share: base color and map, metallic, smoothness, normal, occlusion and emission.
/// </summary>
static class UnityMaterialConverter
{
    public static void Convert(UnityPackageAsset asset, string relative, UnityImportContext context)
    {
        var objects = UnityYaml.ParseObjects(File.ReadAllText(asset.AssetFile!));
        var material = objects.FirstOrDefault(static o => o.TypeName == "Material")
                       ?? throw new InvalidDataException("the file holds no Material");
        var saved = material.Body["m_SavedProperties"];
        var textures = Properties(saved?["m_TexEnvs"]);
        var floats = Properties(saved?["m_Floats"]);
        var colors = Properties(saved?["m_Colors"]);
        var notes = new List<string>();

        var result = new MaterialAsset
        {
            Id = asset.Guid,
            BaseColorFactor = ReadColor(First(colors, "_BaseColor", "_Color")) ?? Vector4.One,
            MetallicFactor = First(floats, "_Metallic")?.Float() ?? 0f,
            // Unity authors smoothness; glTF-style PBR wants its inverse.
            RoughnessFactor = 1f - (First(floats, "_Smoothness", "_Glossiness")?.Float(0.5f) ?? 0.5f),
            BaseColorTexture = Texture(First(textures, "_BaseMap", "_MainTex"), context, notes),
            NormalTexture = Texture(First(textures, "_BumpMap", "_NormalMap"), context, notes),
            OcclusionTexture = Texture(First(textures, "_OcclusionMap"), context, notes),
            EmissiveTexture = Texture(First(textures, "_EmissionMap"), context, notes),
            MetallicRoughnessTexture = Texture(First(textures, "_MetallicGlossMap"), context, notes),
        };
        if (ReadColor(First(colors, "_EmissionColor")) is { } emission) result.EmissiveFactor = new Vector3(emission.X, emission.Y, emission.Z);
        if (result.MetallicRoughnessTexture is not null) notes.Add("metallic map: Unity packs smoothness in alpha, glTF packs roughness in green");

        var target = context.Place(Path.ChangeExtension(relative, ".material"));
        Serializer.Save(target, result);
        context.WriteMeta(target, new MaterialAsset { Id = asset.Guid, RelativePath = context.MetaPath(target) });
        context.Report.Converted.Add(new UnityImportEntry(asset.Pathname, context.Relative(target), notes.Count == 0 ? null : string.Join("; ", notes)));
    }

    /// <summary>A list of single-entry maps (<c>- _Name: value</c>) as a dictionary by name.</summary>
    static Dictionary<string, UnityYamlValue> Properties(UnityYamlValue? sequence)
    {
        var properties = new Dictionary<string, UnityYamlValue>(StringComparer.Ordinal);
        foreach (var item in sequence?.Items ?? [])
        {
            if (item is UnityYamlMap { Entries.Count: > 0 } map) properties[map.Entries[0].Key] = map.Entries[0].Value;
        }

        return properties;
    }

    static UnityYamlValue? First(Dictionary<string, UnityYamlValue> properties, params string[] names) =>
        names.Select(n => properties.GetValueOrDefault(n)).FirstOrDefault(static v => v is not null);

    static Vector4? ReadColor(UnityYamlValue? value) =>
        value is null || value["r"] is null ? null : new Vector4(value["r"]!.Float(1f), value["g"]!.Float(1f), value["b"]!.Float(1f), value["a"]?.Float(1f) ?? 1f);

    static AssetReference<TextureAsset>? Texture(UnityYamlValue? property, UnityImportContext context, List<string> notes)
    {
        if (property?["m_Texture"] is not { } texture || !Guid.TryParseExact(texture["guid"]?.Text ?? string.Empty, "N", out var guid)
            || guid == Guid.Empty)
            return null;

        if (context.Has(guid)) return new AssetReference<TextureAsset>(guid);

        notes.Add($"texture {guid:N} is not in this package");
        return null;
    }
}
