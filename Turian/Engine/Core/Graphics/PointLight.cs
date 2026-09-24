namespace Turian.Engine.Core;

/// <summary>
/// Represents a point light source in 3D space.
/// </summary>
public struct PointLight : IEquatable<PointLight>
{
    Vector4 position = Vector4.Zero;
    Vector4 color = Vector4.One;

    /// <summary>
    /// Initializes a new instance of the <see cref="PointLight"/> struct with default values.
    /// </summary>
    public PointLight() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="PointLight"/> struct with the specified position and color.
    /// </summary>
    /// <param name="position">The position of the point light.</param>
    /// <param name="color">The color of the point light.</param>
    public PointLight(Vector4 position, Vector4 color)
    {
        this.position = position;
        this.color = color;
    }

    /// <summary>
    /// Sets the position of the point light.
    /// </summary>
    /// <param name="pos">The new position of the point light.</param>
    public void SetPosition(Vector3 pos) =>
        position = new Vector4(pos.X, pos.Y, pos.Z, 0f);

    /// <summary>
    /// Sets the color and intensity of the point light.
    /// </summary>
    /// <param name="col">The color of the point light.</param>
    /// <param name="intensity">The intensity of the point light.</param>
    public void SetColor(Vector4 col, float intensity) =>
        color = new Vector4(col.X, col.Y, col.Z, intensity);

    /// <summary>
    /// Converts the <see cref="PointLight"/> to a byte array.
    /// </summary>
    /// <returns>A byte array representing the <see cref="PointLight"/>.</returns>
    public readonly byte[] GetAsBytes()
    {
        var bytes = new byte[32];
        position.AsBytes().CopyTo(bytes, 0);
        color.AsBytes().CopyTo(bytes, 16);

        return bytes;
    }

    /// <summary>
    /// Gets the size of the <see cref="PointLight"/> in bytes.
    /// </summary>
    /// <returns>The size of the <see cref="PointLight"/> in bytes.</returns>
    public static uint SizeOf => (uint)Unsafe.SizeOf<PointLight>();

    /// <inheritdoc />
    public override readonly string ToString()
    {
        return $"p:{position}, c:{color}";
    }

    /// <summary>
    /// Indicates whether this <see cref="PointLight"/> is equal to another one.
    /// </summary>
    /// <param name="other">The point light to compare with this one.</param>
    /// <returns><c>true</c> if the two lights are equal; otherwise, <c>false</c>.</returns>
    public readonly bool Equals(PointLight other) => position.Equals(other.position) && color.Equals(other.color);

    /// <inheritdoc />
    public override readonly bool Equals(object? obj) => obj is PointLight other && Equals(other);

    /// <summary>
    /// Indicates whether two <see cref="PointLight"/> instances are equal.
    /// </summary>
    /// <param name="left">The first point light to compare.</param>
    /// <param name="right">The second point light to compare.</param>
    /// <returns><c>true</c> if the two lights are equal; otherwise, <c>false</c>.</returns>
    public static bool operator ==(PointLight left, PointLight right) => left.Equals(right);

    /// <summary>
    /// Indicates whether two <see cref="PointLight"/> instances are not equal.
    /// </summary>
    /// <param name="left">The first point light to compare.</param>
    /// <param name="right">The second point light to compare.</param>
    /// <returns><c>true</c> if the two lights are not equal; otherwise, <c>false</c>.</returns>
    public static bool operator !=(PointLight left, PointLight right) => !left.Equals(right);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(position, color);
}
