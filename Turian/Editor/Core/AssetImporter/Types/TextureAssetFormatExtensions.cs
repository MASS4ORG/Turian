namespace Turian.Editor.Core;

static class TextureAssetFormatExtensions
{
    public static TextureAssetFormat FromFilePath(string filePath)
    {
        return Path.GetExtension(filePath).ToUpperInvariant() switch
        {
            ".PNG" => TextureAssetFormat.Png,
            ".JPG" => TextureAssetFormat.Jpeg,
            ".JPEG" => TextureAssetFormat.Jpeg,
            ".TGA" => TextureAssetFormat.Tga,
            ".BMP" => TextureAssetFormat.Bmp,
            ".GIF" => TextureAssetFormat.Gif,
            ".WEBP" => TextureAssetFormat.Webp,
            ".DDS" => TextureAssetFormat.Dds,
            _ => TextureAssetFormat.Unknown
        };
    }
}
