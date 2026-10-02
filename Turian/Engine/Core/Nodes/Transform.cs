
namespace Turian.Engine.Core;

/// <summary>
/// An immutable position, orientation and scale in 3D space. Edit it with <c>with</c> expressions and assign it back.
/// </summary>
public readonly record struct Transform : IFormattable
{
    /// <summary>The transform that leaves points unchanged.</summary>
    public static readonly Transform Identity = new();

    /// <summary>Initializes a transform at the origin with no rotation and unit scale.</summary>
    public Transform()
    {
    }

    /// <summary>Gets the position of the transformation.</summary>
    public Vector3 Position { get; init; } = Vector3.Zero;

    /// <summary>Gets the orientation of the transformation as a quaternion.</summary>
    [Hide]
    public Quaternion Orientation { get; init; } = Quaternion.Identity;

    /// <summary>
    /// Gets the orientation as Euler angles in degrees. Calculated from <see cref="Orientation"/> on each read.
    /// </summary>
    [JsonIgnore]
    public Vector3 Rotation
    {
        get => Orientation.ToEulerDegrees();
        init => Orientation = value.ToQuaternion();
    }

    /// <summary>Gets the scale of the transformation.</summary>
    public Vector3 Scale { get; init; } = Vector3.One;

    /// <summary>Combines this local transformation with its parent's global one, giving this one's global.</summary>
    /// <param name="parent">The parent's global transformation.</param>
    /// <returns>The global transformation.</returns>
    public Transform AddParent(in Transform parent) => new()
    {
        Position = Vector3.Transform(Position * parent.Scale, parent.Orientation) + parent.Position,
        Orientation = parent.Orientation * Orientation,
        Scale = parent.Scale * Scale,
    };

    /// <summary>Expresses this global transformation relative to a parent's global one, giving the local.</summary>
    /// <param name="parent">The parent's global transformation.</param>
    /// <returns>The local transformation.</returns>
    public Transform RemoveParent(in Transform parent)
    {
        var inverse = Quaternion.Inverse(parent.Orientation);
        return new()
        {
            Position = Vector3.Transform(Position - parent.Position, inverse) / parent.Scale,
            Orientation = inverse * Orientation,
            Scale = Scale / parent.Scale,
        };
    }

    /// <summary>Gets the 4x4 model matrix: scale, then rotation, then translation.</summary>
    /// <returns>The 4x4 transformation matrix.</returns>
    public Matrix4x4 Matrix4X4()
    {
        var matrix = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Orientation);
        matrix.Translation = Position;
        return matrix;
    }

    /// <summary>Gets the 4x4 normal matrix: inverse scale, then rotation, without translation.</summary>
    /// <returns>The 4x4 normal matrix.</returns>
    public Matrix4x4 NormalMatrix() =>
        Matrix4x4.CreateScale(Vector3.One / Scale) * Matrix4x4.CreateFromQuaternion(Orientation);

    /// <summary>Gets a value indicating whether this transformation is the identity transformation.</summary>
    public bool IsIdentity => this == Identity;

    /// <summary>Gets the forward vector in the local space of this transformation.</summary>
    public Vector3 Forward => Vector3.Transform(Mathf.Forward, Orientation);

    /// <summary>Gets the backward vector in the local space of this transformation.</summary>
    public Vector3 Backward => Vector3.Transform(Mathf.Backward, Orientation);

    /// <summary>Gets the up vector in the local space of this transformation.</summary>
    public Vector3 Up => Vector3.Transform(Mathf.Up, Orientation);

    /// <summary>Gets the down vector in the local space of this transformation.</summary>
    public Vector3 Down => Vector3.Transform(Mathf.Down, Orientation);

    /// <summary>Gets the left vector in the local space of this transformation.</summary>
    public Vector3 Left => Vector3.Transform(Mathf.Left, Orientation);

    /// <summary>Gets the right vector in the local space of this transformation.</summary>
    public Vector3 Right => Vector3.Transform(Mathf.Right, Orientation);

    /// <summary>Converts this <see cref="Transform"/> to its string representation.</summary>
    /// <returns>A string representation of the transformation.</returns>
    public override string ToString() =>
        string.Format(CultureInfo.CurrentCulture, "Position:{0} Orientation:{1} Scale:{2}", Position, Orientation,
            Scale);

    /// <summary>Converts this <see cref="Transform"/> to its string representation using a format and provider.</summary>
    /// <param name="format">The format applied to the vectors.</param>
    /// <param name="formatProvider">The culture-specific formatting information.</param>
    /// <returns>A string representation of the transformation.</returns>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (format == null || formatProvider == null)
        {
            return ToString();
        }

        return string.Format(
            formatProvider,
            "Position:{0} Orientation:{1} Scale:{2}",
            Position.ToString(format, formatProvider),
            Orientation.ToString(),
            Scale.ToString(format, formatProvider)
        );
    }
}
