using SkiaSharp;

namespace Turian.Engine.Core;

/// <summary>A rasterized text distance field and its em-relative dimensions.</summary>
sealed record SdfTextLayout(int Width, int Height, byte[] Rgba, Vector2 EmSize);

/// <summary>Builds cached text masks from the same Skia font files and family names used by Guinevere.</summary>
static class SdfTextRasterizer
{
    const int em = 48;
    const int padding = 10;
    const int maximumExtent = 4096;

    internal static SdfTextLayout Rasterize(string text, string fontReference, AssetDatabase assets)
    {
        using var face = LoadTypeface(fontReference, assets);
        using var font = new SKFont(face, em);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var metrics = font.Metrics;
        var lineHeight = metrics.Descent - metrics.Ascent + metrics.Leading;
        var width = padding * 2f;
        var left = 0f;
        foreach (var line in lines)
        {
            var advance = font.MeasureText(line, out var ink);
            left = MathF.Min(left, ink.Left);
            width = MathF.Max(width, MathF.Max(advance, ink.Right) + padding * 2);
        }
        width -= left;
        var height = lineHeight * lines.Length + padding * 2;
        ValidateExtent(width, height);
        using var bitmap = new SKBitmap((int)MathF.Ceiling(width), (int)MathF.Ceiling(height),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            for (var i = 0; i < lines.Length; i++)
            {
                canvas.DrawText(lines[i], padding - left, padding - metrics.Ascent + i * lineHeight,
                    SKTextAlign.Left, font, paint);
            }
        }
        var pixels = bitmap.Bytes;
        var mask = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                mask[y * bitmap.Width + x] = pixels[y * bitmap.RowBytes + x * 4 + 3];
            }
        }
        var sdf = SignedDistanceField.Build(mask, bitmap.Width, bitmap.Height);
        var rgba = new byte[sdf.Length * 4];
        for (var i = 0; i < sdf.Length; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255;
            rgba[i * 4 + 3] = sdf[i];
        }
        return new(bitmap.Width, bitmap.Height, rgba, new Vector2(bitmap.Width, bitmap.Height) / em);
    }

    static void ValidateExtent(float width, float height)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width > maximumExtent || height > maximumExtent)
        {
            throw new ArgumentException("Text exceeds the maximum distance-field extent of 4096 pixels.");
        }
    }

    static SKTypeface LoadTypeface(string reference, AssetDatabase assets)
    {
        if (reference.StartsWith("asset://", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(reference.AsSpan(8), out var id))
        {
            return FromAsset(id, assets);
        }
        var record = assets.GetAssetsSnapshot().FirstOrDefault(record =>
            string.Equals(record.SourceRelativePath.Replace('\\', '/'), reference.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase));
        if (record is not null)
        {
            return FromAsset(record.AssetId, assets);
        }
        return SKTypeface.FromFamilyName(reference);
    }

    static SKTypeface FromAsset(Guid id, AssetDatabase assets)
    {
        if (!assets.TryGetAssetProvider(id, out var provider) || provider is null)
        {
            throw new FileNotFoundException($"Font asset {id} is not available.");
        }
        using var stream = provider.GetAssetStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var data = SKData.CreateCopy(buffer.ToArray());
        return SKTypeface.FromData(data) ?? throw new InvalidDataException("The asset is not a supported Skia font.");
    }
}
