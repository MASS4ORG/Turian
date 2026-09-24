namespace Turian.Editor.Core;

static class ModelAssetFormatExtensions
{
    public static ModelAssetFormat FromFilePath(string filePath)
    {
        return Path.GetExtension(filePath).ToUpperInvariant() switch
        {
            ".OBJ" => ModelAssetFormat.Obj,
            ".FBX" => ModelAssetFormat.Fbx,
            ".GLTF" => ModelAssetFormat.Gltf,
            ".GLB" => ModelAssetFormat.Glb,
            ".DAE" => ModelAssetFormat.Dae,
            ".BLEND" => ModelAssetFormat.Blend,
            _ => ModelAssetFormat.Unknown
        };
    }
}
