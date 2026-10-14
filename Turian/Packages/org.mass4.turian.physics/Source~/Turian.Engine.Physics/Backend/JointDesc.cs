namespace Turian.Engine.Physics;

/// <summary>The kind of constraint a <see cref="JointDesc"/> creates.</summary>
public enum JointType
{
    /// <summary>Locks all relative movement.</summary>
    Fixed,

    /// <summary>Allows rotation around one axis.</summary>
    Hinge,

    /// <summary>Keeps the anchors together and allows any rotation.</summary>
    BallSocket,

    /// <summary>Allows sliding along one axis.</summary>
    Slider,

    /// <summary>Keeps the anchors within a distance range.</summary>
    Distance,

    /// <summary>Pulls the anchors towards a rest distance with a spring.</summary>
    Spring,
}

/// <summary>A range in radians or meters; disabled limits allow any value.</summary>
/// <param name="Enabled">Whether the range is enforced.</param>
/// <param name="Min">The lowest value.</param>
/// <param name="Max">The highest value.</param>
public readonly record struct JointLimit(bool Enabled, float Min, float Max);

/// <summary>Drives a joint towards a velocity with bounded force.</summary>
/// <param name="Enabled">Whether the motor runs.</param>
/// <param name="TargetVelocity">Radians or meters per second.</param>
/// <param name="MaxForce">The largest force or torque the motor applies.</param>
public readonly record struct JointMotor(bool Enabled, float TargetVelocity, float MaxForce);

/// <summary>Pulls a joint towards a target position.</summary>
/// <param name="Enabled">Whether the spring acts.</param>
/// <param name="Frequency">The spring's oscillation frequency in hertz; higher is stiffer.</param>
/// <param name="DampingRatio">1 settles without overshoot; 0 keeps oscillating.</param>
/// <param name="Target">The rest angle in radians or distance in meters.</param>
public readonly record struct JointSpring(bool Enabled, float Frequency, float DampingRatio, float Target);

/// <summary>Describes a constraint between two bodies, or between one body and the world.</summary>
public readonly record struct JointDesc
{
    /// <summary>The kind of constraint.</summary>
    public JointType Type { get; init; }

    /// <summary>The first body; it must be valid.</summary>
    public BodyHandle BodyA { get; init; }

    /// <summary>The second body, or an invalid handle to attach <see cref="BodyA"/> to the world.</summary>
    public BodyHandle BodyB { get; init; }

    /// <summary>The anchor relative to the first body's origin.</summary>
    public Vector3 AnchorA { get; init; }

    /// <summary>The anchor relative to the second body's origin, or in world space without a second body.</summary>
    public Vector3 AnchorB { get; init; }

    /// <summary>The hinge or slider axis relative to the first body.</summary>
    public Vector3 AxisA { get; init; }

    /// <summary>The hinge or slider axis relative to the second body, or in world space without one.</summary>
    public Vector3 AxisB { get; init; }

    /// <summary>The rotation of the second body relative to the first that a fixed joint keeps.</summary>
    public Quaternion RelativeOrientation { get; init; }

    /// <summary>Whether the connected bodies still collide with each other.</summary>
    public bool EnableCollision { get; init; }

    /// <summary>The angle, swing, distance or slide range, depending on the type.</summary>
    public JointLimit Limit { get; init; }

    /// <summary>A hinge or slider motor.</summary>
    public JointMotor Motor { get; init; }

    /// <summary>A hinge, slider or spring joint spring.</summary>
    public JointSpring Spring { get; init; }
}
