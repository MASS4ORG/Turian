namespace Turian.Engine.Physics;

/// <summary>Locks this body to the connected one, or to the world, keeping their relative pose.</summary>
[ComponentContextMenu("Physics/Fixed Joint")]
[TypeId("dcb45716-7540-4279-aed7-072fca73fd66")]
public class FixedJointComponent : JointComponent
{
    /// <inheritdoc />
    public override JointDesc Describe(out Vector3 worldAxis)
    {
        worldAxis = Vector3.UnitX;
        return new JointDesc { Type = JointType.Fixed };
    }
}

/// <summary>Lets this body turn around one axis, as a door or a wheel does, with an optional limit, motor or spring.</summary>
[ComponentContextMenu("Physics/Hinge Joint")]
[TypeId("d7ffc4c9-a5b1-4c23-bcd6-f40f80c46ae8")]
public class HingeJointComponent : JointComponent
{
    /// <summary>The hinge axis in this node's local space.</summary>
    public Vector3 Axis
    {
        get;
        set => Change(ref field, value);
    } = Vector3.UnitX;

    /// <summary>Whether <see cref="MinAngle"/> and <see cref="MaxAngle"/> are enforced.</summary>
    public bool UseLimits
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The lowest angle in degrees.</summary>
    public float MinAngle
    {
        get;
        set => Change(ref field, value);
    } = -45f;

    /// <summary>The highest angle in degrees.</summary>
    public float MaxAngle
    {
        get;
        set => Change(ref field, value);
    } = 45f;

    /// <summary>Whether the motor drives the hinge.</summary>
    public bool UseMotor
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The motor's target speed in degrees per second.</summary>
    public float MotorSpeed
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The largest torque the motor applies, in newton-meters.</summary>
    public float MotorForce
    {
        get;
        set => Change(ref field, value);
    } = 10f;

    /// <summary>Whether a spring pulls the hinge towards <see cref="SpringTargetAngle"/>.</summary>
    public bool UseSpring
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The spring's oscillation frequency in hertz; higher is stiffer.</summary>
    public float SpringFrequency
    {
        get;
        set => Change(ref field, value);
    } = 2f;

    /// <summary>The spring's damping ratio: 1 settles without overshoot, 0 keeps swinging.</summary>
    public float SpringDamping
    {
        get;
        set => Change(ref field, value);
    } = 1f;

    /// <summary>The angle in degrees the spring pulls towards.</summary>
    public float SpringTargetAngle
    {
        get;
        set => Change(ref field, value);
    }

    /// <inheritdoc />
    public override JointDesc Describe(out Vector3 worldAxis)
    {
        worldAxis = WorldAxis(Axis);
        return new JointDesc
        {
            Type = JointType.Hinge,
            Limit = new JointLimit(UseLimits, Radians(MathF.Min(MinAngle, MaxAngle)), Radians(MathF.Max(MinAngle, MaxAngle))),
            Motor = new JointMotor(UseMotor, Radians(MotorSpeed), MotorForce),
            Spring = new JointSpring(UseSpring, SpringFrequency, SpringDamping, Radians(SpringTargetAngle)),
        };
    }
}

/// <summary>Lets this body slide along one axis without turning, as a drawer or a piston does.</summary>
[ComponentContextMenu("Physics/Slider Joint")]
[TypeId("32cedc73-b2d2-4749-be3f-114034708f43")]
public class SliderJointComponent : JointComponent
{
    /// <summary>The slide axis in this node's local space.</summary>
    public Vector3 Axis
    {
        get;
        set => Change(ref field, value);
    } = Vector3.UnitX;

    /// <summary>Whether <see cref="MinDistance"/> and <see cref="MaxDistance"/> are enforced.</summary>
    public bool UseLimits
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The lowest offset along the axis in meters.</summary>
    public float MinDistance
    {
        get;
        set => Change(ref field, value);
    } = -1f;

    /// <summary>The highest offset along the axis in meters.</summary>
    public float MaxDistance
    {
        get;
        set => Change(ref field, value);
    } = 1f;

    /// <summary>Whether the motor drives the slider.</summary>
    public bool UseMotor
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The motor's target speed in meters per second.</summary>
    public float MotorSpeed
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The largest force the motor applies, in newtons.</summary>
    public float MotorForce
    {
        get;
        set => Change(ref field, value);
    } = 10f;

    /// <inheritdoc />
    public override JointDesc Describe(out Vector3 worldAxis)
    {
        worldAxis = WorldAxis(Axis);
        return new JointDesc
        {
            Type = JointType.Slider,
            Limit = new JointLimit(UseLimits, MathF.Min(MinDistance, MaxDistance), MathF.Max(MinDistance, MaxDistance)),
            Motor = new JointMotor(UseMotor, MotorSpeed, MotorForce),
        };
    }
}

/// <summary>Keeps the anchors together while this body turns freely, optionally within a cone around an axis.</summary>
[ComponentContextMenu("Physics/Ball Socket Joint")]
[TypeId("de0d78ed-f2ba-4f63-a258-600e253084f6")]
public class BallSocketJointComponent : JointComponent
{
    /// <summary>The axis the swing limit's cone opens around, in this node's local space.</summary>
    public Vector3 SwingAxis
    {
        get;
        set => Change(ref field, value);
    } = -Vector3.UnitY;

    /// <summary>Whether <see cref="SwingLimit"/> is enforced.</summary>
    public bool UseSwingLimit
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The cone's half angle in degrees.</summary>
    public float SwingLimit
    {
        get;
        set => Change(ref field, value);
    } = 45f;

    /// <inheritdoc />
    public override JointDesc Describe(out Vector3 worldAxis)
    {
        worldAxis = WorldAxis(SwingAxis);
        return new JointDesc
        {
            Type = JointType.BallSocket, Limit = new JointLimit(UseSwingLimit, 0f, Radians(MathF.Max(SwingLimit, 0f))),
        };
    }
}

/// <summary>Keeps the anchors between a minimum and a maximum distance, as a rope or a rod does.</summary>
[ComponentContextMenu("Physics/Distance Joint")]
[TypeId("b5eeb870-cfcd-40d5-ac8a-bd23ed6df271")]
public class DistanceJointComponent : JointComponent
{
    /// <summary>The shortest distance between the anchors in meters.</summary>
    public float MinDistance
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The longest distance between the anchors in meters; a negative value uses their distance at creation.</summary>
    public float MaxDistance
    {
        get;
        set => Change(ref field, value);
    } = -1f;

    /// <inheritdoc />
    public override JointDesc Describe(out Vector3 worldAxis)
    {
        worldAxis = Vector3.UnitX;
        return new JointDesc
        {
            Type = JointType.Distance, Limit = new JointLimit(true, MathF.Max(MinDistance, 0f), MaxDistance),
        };
    }
}

/// <summary>Pulls the anchors towards a rest distance with a spring, optionally within a distance range.</summary>
[ComponentContextMenu("Physics/Spring Joint")]
[TypeId("d3572447-0875-49ae-87b1-c164fc0a50d3")]
public class SpringJointComponent : JointComponent
{
    /// <summary>The spring's oscillation frequency in hertz; higher is stiffer.</summary>
    public float Frequency
    {
        get;
        set => Change(ref field, value);
    } = 2f;

    /// <summary>The spring's damping ratio: 1 settles without overshoot, 0 keeps bouncing.</summary>
    public float DampingRatio
    {
        get;
        set => Change(ref field, value);
    } = 0.3f;

    /// <summary>The distance the spring pulls towards in meters; a negative value uses the distance at creation.</summary>
    public float RestDistance
    {
        get;
        set => Change(ref field, value);
    } = -1f;

    /// <summary>The shortest distance between the anchors in meters.</summary>
    public float MinDistance
    {
        get;
        set => Change(ref field, value);
    }

    /// <summary>The longest distance between the anchors in meters; zero or less leaves it unlimited.</summary>
    public float MaxDistance
    {
        get;
        set => Change(ref field, value);
    }

    /// <inheritdoc />
    public override JointDesc Describe(out Vector3 worldAxis)
    {
        worldAxis = Vector3.UnitX;
        return new JointDesc
        {
            Type = JointType.Spring,
            Spring = new JointSpring(true, Frequency, DampingRatio, RestDistance),
            Limit = new JointLimit(MinDistance > 0f || MaxDistance > 0f, MathF.Max(MinDistance, 0f),
                MaxDistance > 0f ? MaxDistance : float.MaxValue),
        };
    }
}
