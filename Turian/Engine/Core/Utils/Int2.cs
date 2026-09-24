namespace Turian.Engine.Core;

/// <summary>
/// A two-component integer vector, used for pixel sizes and resolutions that have no reason to
/// carry a generic Silk.NET math type.
/// </summary>
public struct Int2(int x, int y) : IEquatable<Int2>
{
    /// <summary>The X component.</summary>
    public int X = x;

    /// <summary>The Y component.</summary>
    public int Y = y;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Int2 other && Equals(other);

    /// <inheritdoc/>
    public readonly bool Equals(Int2 other) => X == other.X && Y == other.Y;

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);

    /// <inheritdoc/>
    public override readonly string ToString() => $"({X}, {Y})";

    /// <summary>Component-wise equality.</summary>
    public static bool operator ==(Int2 left, Int2 right) => left.Equals(right);

    /// <summary>Component-wise inequality.</summary>
    public static bool operator !=(Int2 left, Int2 right) => !left.Equals(right);
}
