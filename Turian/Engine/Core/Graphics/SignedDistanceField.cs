namespace Turian.Engine.Core;

/// <summary>Converts a glyph coverage mask into an eight-neighbor signed distance field.</summary>
public static class SignedDistanceField
{
    /// <summary>Encodes the interior above 128 and exterior below 128, saturating at the supplied spread.</summary>
    public static byte[] Build(ReadOnlySpan<byte> coverage, int width, int height, float spread = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        CurveValidation.Finite(spread);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spread);
        if (coverage.Length != checked(width * height))
        {
            throw new ArgumentException("Mask size does not match.", nameof(coverage));
        }

        var inside = new float[coverage.Length];
        var outside = new float[coverage.Length];
        for (var i = 0; i < coverage.Length; i++)
        {
            inside[i] = coverage[i] >= 128 ? 0 : 1e6f;
            outside[i] = coverage[i] < 128 ? 0 : 1e6f;
        }
        Transform(inside, width, height);
        Transform(outside, width, height);
        var result = new byte[coverage.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (byte)MathF.Round(Math.Clamp(0.5f + (outside[i] - inside[i]) / (spread * 2), 0, 1) * 255);
        }

        return result;
    }

    static void Transform(float[] values, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Relax(values, width, height, x, y, -1);
            }
        }

        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = width - 1; x >= 0; x--)
            {
                Relax(values, width, height, x, y, 1);
            }
        }
    }

    static void Relax(float[] values, int width, int height, int x, int y, int step)
    {
        var index = y * width + x;
        values[index] = MathF.Min(values[index], At(values, width, height, x + step, y) + 1);
        values[index] = MathF.Min(values[index], At(values, width, height, x, y + step) + 1);
        values[index] = MathF.Min(values[index], At(values, width, height, x - 1, y + step) + 1.4142136f);
        values[index] = MathF.Min(values[index], At(values, width, height, x + 1, y + step) + 1.4142136f);
    }

    static float At(float[] values, int width, int height, int x, int y) =>
        x < 0 || x >= width || y < 0 || y >= height ? 1e6f : values[y * width + x];
}
