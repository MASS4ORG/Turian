namespace Turian.Engine.Core;

/// <summary>
/// A light with a direction but no position: every surface receives it from the same angle at the
/// same strength, which is how sunlight and moonlight behave at world scale.
/// </summary>
public struct DirectionalLight
{
    Vector4 direction = Vector4.Zero;
    Vector4 color = Vector4.Zero;

    /// <summary>Creates a light that emits nothing until it is configured.</summary>
    public DirectionalLight() { }

    /// <summary>
    /// Sets the direction the light travels, normalized. This is the node's forward axis, so a sun
    /// overhead points down.
    /// </summary>
    /// <param name="value">The direction light travels in world space.</param>
    public void SetDirection(Vector3 value)
    {
        var normalized = value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.Zero;
        direction = new Vector4(normalized.X, normalized.Y, normalized.Z, 0f);
    }

    /// <summary>Sets the light color and intensity. Intensity zero disables the slot.</summary>
    /// <param name="col">The light color.</param>
    /// <param name="intensity">The light intensity.</param>
    public void SetColor(Vector4 col, float intensity) =>
        color = new Vector4(col.X, col.Y, col.Z, intensity);

    /// <summary>Turns the light off, so the shader skips its slot.</summary>
    public void Clear()
    {
        direction = Vector4.Zero;
        color = Vector4.Zero;
    }

    /// <summary>Converts the light to its std140 byte layout.</summary>
    /// <returns>A 32-byte array holding the direction and color.</returns>
    public readonly byte[] GetAsBytes()
    {
        var bytes = new byte[32];
        direction.AsBytes().CopyTo(bytes, 0);
        color.AsBytes().CopyTo(bytes, 16);

        return bytes;
    }

    /// <summary>Gets the size of the light in bytes.</summary>
    public static uint SizeOf => (uint)Unsafe.SizeOf<DirectionalLight>();

    /// <inheritdoc />
    public override readonly string ToString() => $"d:{direction}, c:{color}";
}
