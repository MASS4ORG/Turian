namespace Turian.Engine.Physics;

/// <summary>
/// How much grip a tyre has for a slip speed, as Unity's WheelFrictionCurve: grip rises to an extremum, falls to an
/// asymptote as the tyre slides, and is scaled by <see cref="Stiffness"/>. Grip times the wheel's load is the force.
/// </summary>
public record struct WheelFrictionCurve
{
    /// <summary>The slip speed, in meters per second, of the greatest grip.</summary>
    public float ExtremumSlip { get; set; }

    /// <summary>The greatest grip, as a multiple of the wheel's load.</summary>
    public float ExtremumValue { get; set; }

    /// <summary>The slip speed from which the tyre slides with constant grip.</summary>
    public float AsymptoteSlip { get; set; }

    /// <summary>The grip of a sliding tyre.</summary>
    public float AsymptoteValue { get; set; }

    /// <summary>Scales the whole curve; lower it for ice, raise it for racing tyres.</summary>
    public float Stiffness { get; set; }

    /// <summary>Unity's default forward curve.</summary>
    public static WheelFrictionCurve Forward => new()
    {
        ExtremumSlip = 0.4f,
        ExtremumValue = 1f,
        AsymptoteSlip = 0.8f,
        AsymptoteValue = 0.5f,
        Stiffness = 1f,
    };

    /// <summary>Unity's default sideways curve.</summary>
    public static WheelFrictionCurve Sideways => new()
    {
        ExtremumSlip = 0.2f,
        ExtremumValue = 1f,
        AsymptoteSlip = 0.5f,
        AsymptoteValue = 0.75f,
        Stiffness = 1f,
    };

    /// <summary>The grip at a slip speed of either sign.</summary>
    public readonly float Evaluate(float slip)
    {
        var speed = MathF.Abs(slip);
        var grip = speed <= ExtremumSlip
            ? (ExtremumSlip > 0f ? ExtremumValue * speed / ExtremumSlip : ExtremumValue)
            : speed < AsymptoteSlip
                ? float.Lerp(ExtremumValue, AsymptoteValue, (speed - ExtremumSlip) / (AsymptoteSlip - ExtremumSlip))
                : AsymptoteValue;
        return grip * Stiffness;
    }

    /// <summary>The curve's greatest grip.</summary>
    public readonly float Peak => MathF.Max(ExtremumValue, AsymptoteValue) * Stiffness;
}

/// <summary>Where and how a wheel touched the ground during the last fixed tick.</summary>
/// <param name="Point">The world contact point.</param>
/// <param name="Normal">The ground's normal.</param>
/// <param name="ForwardDir">The rolling direction along the ground.</param>
/// <param name="SidewaysDir">The direction to the wheel's right along the ground.</param>
/// <param name="Force">The suspension force pressing the wheel down, in newtons.</param>
/// <param name="ForwardSlip">The tyre's speed minus the ground's along <paramref name="ForwardDir"/>, in m/s.</param>
/// <param name="SidewaysSlip">The wheel's sliding speed along <paramref name="SidewaysDir"/>, in m/s.</param>
/// <param name="Collider">The ground collider.</param>
public readonly record struct WheelHit(
    Vector3 Point,
    Vector3 Normal,
    Vector3 ForwardDir,
    Vector3 SidewaysDir,
    float Force,
    float ForwardSlip,
    float SidewaysSlip,
    ColliderComponent Collider);
