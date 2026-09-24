
namespace Turian.Engine.Core;

/// <summary>
/// Represents a transformation in 3D space consisting of position, orientation, and scale.
/// </summary>
public class Transform : IEquatable<Transform>, IFormattable, INotifyPropertyChanged
{
    /// <summary>
    /// Triggers when any of Position, Orientation or Scale changed
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises the <see cref="PropertyChanged"/> event to indicate that a property value has changed.
    /// </summary>
    /// <param name="propertyName">The name of the property that has changed.</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    Vector3 position;

    /// <summary>
    /// Gets or sets the position of the transformation.
    /// </summary>
    public Vector3 Position
    {
        get => position;
        set
        {
            position = value;
            OnPropertyChanged(nameof(position));
        }
    }

    Quaternion orientation;

    /// <summary>
    /// Gets or sets the orientation of the transformation as a quaternion.
    /// </summary>
    [HideInEditor]
    public Quaternion Orientation
    {
        get => orientation;
        set
        {
            orientation = value;
            isRotationCacheValid = true;
            OnPropertyChanged(nameof(orientation));
        }
    }

    Vector3 rotationCache; // Cache for Euler angles
    bool isRotationCacheValid; // Flag to check if cache is valid

    /// <summary>
    /// Gets or sets the rotation of the transformation in Euler angles (in degrees).
    /// Its not save internally, it's calculated. More expensive to use than Orientation
    /// </summary>
    [JsonIgnore]
    public Vector3 Rotation
    {
        get
        {
            if (!isRotationCacheValid || true)
            {
                rotationCache = Orientation.ToEulerDegrees();
                isRotationCacheValid = true;
            }
            return rotationCache;
        }
        set
        {
            Orientation = value.ToQuaternion();
            rotationCache = value;
            isRotationCacheValid = true;
        }
    }

    Vector3 scale = Vector3.One;

    /// <summary>
    /// Gets or sets the scale of the transformation.
    /// </summary>
    public Vector3 Scale
    {
        get => scale;
        set
        {
            scale = value;
            OnPropertyChanged(nameof(scale));
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Transform"/> class with default values.
    /// </summary>
    public Transform()
    {
        Position = Vector3.Zero;
        Orientation = Quaternion.Identity;
        Scale = Vector3.One;
    }

    /// <summary>
    /// Adds a parent transformation to this transformation, resulting in a new global transformation.
    /// </summary>
    /// <param name="parent">The parent transformation to add.</param>
    /// <returns>A new <see cref="Transform"/> representing the global transformation.</returns>
    public Transform AddParent(Transform? parent)
    {
        return parent is null
            ? this
            : new Transform
            {
                // Calculate global position.
                Position = parent.Orientation.Rotate(Position * parent.Scale) + parent.Position,
                // Calculate global orientation.
                Orientation = parent.Orientation * Orientation,
                // Calculate global scale.
                Scale = parent.Scale * Scale
            };
    }

    /// <summary>
    /// Removes a parent transformation from this transformation, resulting in a new local transformation.
    /// </summary>
    /// <param name="parent">The parent transformation to remove.</param>
    /// <returns>A new <see cref="Transform"/> representing the local transformation.</returns>
    public Transform RemoveParent(Transform? parent)
    {
        if (parent is null)
        {
            return this;
        }

        var inverse = Quaternion.Inverse(parent.Orientation);
        return new Transform
        {
            // Calculate local position.
            Position = inverse.Rotate((Position - parent.Position) / parent.Scale),
            // Calculate local orientation.
            Orientation = Orientation * inverse,
            // Calculate local scale.
            Scale = Scale / parent.Scale
        };
    }

    /// <summary>
    /// Gets the 4x4 transformation matrix representing this transformation.
    /// </summary>
    /// <returns>The 4x4 transformation matrix.</returns>
    public Matrix4x4 Matrix4X4()
    {
        var vector3 = Rotation * (MathF.PI / 180f);
        var cX = MathF.Cos(vector3.X);
        var sX = MathF.Sin(vector3.X);
        var cY = MathF.Cos(vector3.Y);
        var sY = MathF.Sin(vector3.Y);
        var cZ = MathF.Cos(vector3.Z);
        var sZ = MathF.Sin(vector3.Z);

        return new(
            Scale.X * ((cY * cZ) + (sY * sX * sZ)),
            Scale.X * (cX * sZ),
            Scale.X * ((cY * sX * sZ) - (cZ * sY)),
            0.0f,
            Scale.Y * ((cZ * sY * sX) - (cY * sZ)),
            Scale.Y * (cX * cZ),
            Scale.Y * ((cY * cZ * sX) + (sY * sZ)),
            0.0f,
            Scale.Z * (cX * sY),
            Scale.Z * (-sX),
            Scale.Z * (cY * cX),
            0.0f,
            Position.X,
            Position.Y,
            Position.Z,
            1.0f
        );
    }

    /// <summary>
    /// Gets the 4x4 normal matrix representing this transformation.
    /// </summary>
    /// <returns>The 4x4 normal matrix.</returns>
    public Matrix4x4 NormalMatrix()
    {
        var vector3 = Rotation * (MathF.PI / 180f);
        var cX = MathF.Cos(vector3.X);
        var sX = MathF.Sin(vector3.X);
        var cY = MathF.Cos(vector3.Y);
        var sY = MathF.Sin(vector3.Y);
        var cZ = MathF.Cos(vector3.Z);
        var sZ = MathF.Sin(vector3.Z);

        var invScale = new Vector3(1f / Scale.X, 1f / Scale.Y, 1f / Scale.Z);

        return new(
            invScale.X * ((cY * cZ) + (sY * sX * sZ)),
            invScale.X * (cX * sZ),
            invScale.X * ((cY * sX * sZ) - (cZ * sY)),
            0.0f,
            invScale.Y * ((cZ * sY * sX) - (cY * sZ)),
            invScale.Y * (cX * cZ),
            invScale.Y * ((cY * cZ * sX) + (sY * sZ)),
            0.0f,
            invScale.Z * (cX * sY),
            invScale.Z * (-sX),
            invScale.Z * (cY * cX),
            0.0f,
            0.0f,
            0.0f,
            0.0f,
            1.0f
        );
    }

    static readonly Transform identity = new();

    /// <summary>
    /// Gets a value indicating whether this transformation is the identity transformation.
    /// </summary>
    public bool IsIdentity => Equals(identity);

    /// <summary>
    /// Gets the forward vector in the local space of this transformation.
    /// </summary>
    public Vector3 Forward => Vector3.Transform(Mathf.Forward, Orientation);

    /// <summary>
    /// Gets the backward vector in the local space of this transformation.
    /// </summary>
    public Vector3 Backward => Vector3.Transform(Mathf.Backward, Orientation);

    /// <summary>
    /// Gets the up vector in the local space of this transformation.
    /// </summary>
    public Vector3 Up => Vector3.Transform(Mathf.Up, Orientation);

    /// <summary>
    /// Gets the down vector in the local space of this transformation.
    /// </summary>
    public Vector3 Down => Vector3.Transform(Mathf.Down, Orientation);

    /// <summary>
    /// Gets the left vector in the local space of this transformation.
    /// </summary>
    public Vector3 Left => Vector3.Transform(Mathf.Left, Orientation);

    /// <summary>
    /// Gets the right vector in the local space of this transformation.
    /// </summary>
    public Vector3 Right => Vector3.Transform(Mathf.Right, Orientation);

    /// <summary>
    /// Compares this <see cref="Transform"/> to another <see cref="Transform"/> for equality.
    /// </summary>
    /// <param name="other">The <see cref="Transform"/> to compare to.</param>
    /// <returns>True if the two transformations are equal; otherwise, false.</returns>
    public bool Equals(Transform? other)
    {
        return Position == other?.Position
            && Orientation == other.Orientation
            && Scale == other.Scale;
    }

    /// <summary>
    /// Converts this <see cref="Transform"/> to its string representation.
    /// </summary>
    /// <returns>A string representation of the transformation.</returns>
    public override string ToString()
    {
        return string.Format(
            CultureInfo.CurrentCulture,
            "Position:{0} Orientation:{1} Scale:{2}",
            Position,
            Orientation,
            Scale
        );
    }

    /// <summary>
    /// Converts this <see cref="Transform"/> to its string representation using the specified format and format provider.
    /// </summary>
    /// <param name="format">A format string (not used in this implementation).</param>
    /// <param name="formatProvider">An <see cref="IFormatProvider"/> (not used in this implementation).</param>
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

    /// <summary>
    /// Sets the X-coordinate of the position.
    /// </summary>
    /// <param name="x">The new X-coordinate value.</param>
    public void SetPositionX(float x) => Position = new Vector3(x, Position.Y, Position.Z);

    /// <summary>
    /// Sets the Y-coordinate of the position.
    /// </summary>
    /// <param name="y">The new Y-coordinate value.</param>
    public void SetPositionY(float y) => Position = new Vector3(Position.X, y, Position.Z);

    /// <summary>
    /// Sets the Z-coordinate of the position.
    /// </summary>
    /// <param name="z">The new Z-coordinate value.</param>
    public void SetPositionZ(float z) => Position = new Vector3(Position.X, Position.Y, z);

    /// <summary>
    /// Sets the X component of the orientation quaternion.
    /// </summary>
    /// <param name="x">The new value for the X component.</param>
    public void SetOrientationX(float x) =>
        Orientation = new Quaternion(x, Orientation.Y, Orientation.Z, Orientation.W);

    /// <summary>
    /// Sets the Y component of the orientation quaternion.
    /// </summary>
    /// <param name="y">The new value for the Y component.</param>
    public void SetOrientationY(float y) =>
        Orientation = new Quaternion(Orientation.X, y, Orientation.Z, Orientation.W);

    /// <summary>
    /// Sets the Z component of the orientation quaternion.
    /// </summary>
    /// <param name="z">The new value for the Z component.</param>
    public void SetOrientationZ(float z) =>
        Orientation = new Quaternion(Orientation.X, Orientation.Y, z, Orientation.W);

    /// <summary>
    /// Sets the X component of the rotation in degrees.
    /// </summary>
    /// <param name="x">The new value for the X component of rotation.</param>
    public void SetRotationX(float x) => Rotation = new Vector3(x, Rotation.Y, Rotation.Z);

    /// <summary>
    /// Sets the Y component of the rotation in degrees.
    /// </summary>
    /// <param name="y">The new value for the Y component of rotation.</param>
    public void SetRotationY(float y) => Rotation = new Vector3(Rotation.X, y, Rotation.Z);

    /// <summary>
    /// Sets the Z component of the rotation in degrees.
    /// </summary>
    /// <param name="z">The new value for the Z component of rotation.</param>
    public void SetRotationZ(float z) => Rotation = new Vector3(Rotation.X, Rotation.Y, z);

    /// <summary>
    /// Sets the X component of the scale.
    /// </summary>
    /// <param name="x">The new value for the X component of scale.</param>
    public void SetScaleX(float x) => Scale = new Vector3(x, Scale.Y, Scale.Z);

    /// <summary>
    /// Sets the Y component of the scale.
    /// </summary>
    /// <param name="y">The new value for the Y component of scale.</param>
    public void SetScaleY(float y) => Scale = new Vector3(Scale.X, y, Scale.Z);

    /// <summary>
    /// Sets the Z component of the scale.
    /// </summary>
    /// <param name="z">The new value for the Z component of scale.</param>
    public void SetScaleZ(float z) => Scale = new Vector3(Scale.X, Scale.Y, z);
}
