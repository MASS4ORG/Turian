namespace Turian.Engine.Core;

/// <summary>
/// Provides extension methods for converting various data types to byte arrays.
/// </summary>
public static class TypeExtensions
{
    /// <summary>
    /// Converts an integer to a byte array.
    /// </summary>
    /// <param name="i">The integer to convert.</param>
    /// <returns>The byte array representation of the integer.</returns>
    public static byte[] AsBytes(this int i)
    {
        var bytes = new byte[4];
        BitConverter.GetBytes(i).CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>
    /// Converts a float to a byte array.
    /// </summary>
    /// <param name="f">The float to convert.</param>
    /// <returns>The byte array representation of the float.</returns>
    public static byte[] AsBytes(this float f)
    {
        var bytes = new byte[4];
        BitConverter.GetBytes(f).CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>
    /// Converts a <see cref="Vector4"/> to a byte array.
    /// </summary>
    /// <param name="vec">The <see cref="Vector4"/> to convert.</param>
    /// <returns>The byte array representation of the <see cref="Vector4"/>.</returns>
    public static byte[] AsBytes(this Vector4 vec)
    {
        const uint fsize = 4;
        var bytes = new byte[16];
        BitConverter.GetBytes(vec.X).CopyTo(bytes, 0);
        BitConverter.GetBytes(vec.Y).CopyTo(bytes, fsize);
        BitConverter.GetBytes(vec.Z).CopyTo(bytes, 2 * fsize);
        BitConverter.GetBytes(vec.W).CopyTo(bytes, 3 * fsize);

        return bytes;
    }

    /// <summary>
    /// Converts a <see cref="Matrix4x4"/> to a byte array.
    /// </summary>
    /// <param name="mat">The <see cref="Matrix4x4"/> to convert.</param>
    /// <returns>The byte array representation of the <see cref="Matrix4x4"/>.</returns>
    public static byte[] AsBytes(this Matrix4x4 mat)
    {
        uint offset = 0;
        uint fsize = 4;
        var bytes = new byte[64];
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                BitConverter.GetBytes(mat[row, col]).CopyTo(bytes, offset);
                offset += fsize;
            }
        }
        return bytes;
    }

    /// <summary>
    /// Converts an array of <see cref="PointLight"/> to a byte array.
    /// </summary>
    /// <param name="pts">The array of <see cref="PointLight"/> to convert.</param>
    /// <returns>The byte array representation of the array of <see cref="PointLight"/>.</returns>
    public static byte[] AsBytes(this PointLight[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);

        uint offset = 0;
        var bytes = new byte[pts.Length * 32];
        foreach (var light in pts)
        {
            light.GetAsBytes().CopyTo(bytes, offset);
            offset += 32;
        }

        return bytes;
    }

    /// <summary>
    /// Converts an array of <see cref="DirectionalLight"/> to a byte array.
    /// </summary>
    /// <param name="lights">The array of <see cref="DirectionalLight"/> to convert.</param>
    /// <returns>The byte array representation of the array of <see cref="DirectionalLight"/>.</returns>
    public static byte[] AsBytes(this DirectionalLight[] lights)
    {
        ArgumentNullException.ThrowIfNull(lights);

        uint offset = 0;
        var bytes = new byte[lights.Length * 32];
        foreach (var light in lights)
        {
            light.GetAsBytes().CopyTo(bytes, offset);
            offset += 32;
        }

        return bytes;
    }
}
