namespace Turian.Engine.Physics;

/// <summary>How a force passed to <see cref="RigidbodyComponent.AddForce"/> is interpreted, as in Unity.</summary>
public enum ForceMode
{
    /// <summary>A continuous force in newtons, applied over the fixed step and scaled by mass.</summary>
    Force,

    /// <summary>A continuous acceleration, applied over the fixed step regardless of mass.</summary>
    Acceleration,

    /// <summary>An instant impulse in newton-seconds, scaled by mass.</summary>
    Impulse,

    /// <summary>An instant velocity change regardless of mass.</summary>
    VelocityChange,
}

/// <summary>How a body's node follows the simulation between fixed ticks.</summary>
public enum RigidbodyInterpolation
{
    /// <summary>The node shows the pose of the last tick.</summary>
    None,

    /// <summary>The node blends the last two ticks' poses by the frame's position between ticks.</summary>
    Interpolate,
}

/// <summary>How a body detects collisions.</summary>
public enum CollisionDetectionMode
{
    /// <summary>Tests at each step; fast bodies may pass through thin colliders.</summary>
    Discrete,

    /// <summary>Sweeps the body between steps to stop tunneling, at extra cost.</summary>
    Continuous,
}

/// <summary>Axes a body may not move along or rotate around, as in Unity.</summary>
[Flags]
public enum RigidbodyConstraints
{
    /// <summary>No constraints.</summary>
    None = 0,

    /// <summary>No movement along world X.</summary>
    FreezePositionX = AxisLocks.LinearX,

    /// <summary>No movement along world Y.</summary>
    FreezePositionY = AxisLocks.LinearY,

    /// <summary>No movement along world Z.</summary>
    FreezePositionZ = AxisLocks.LinearZ,

    /// <summary>No rotation around the body's X axis.</summary>
    FreezeRotationX = AxisLocks.AngularX,

    /// <summary>No rotation around the body's Y axis.</summary>
    FreezeRotationY = AxisLocks.AngularY,

    /// <summary>No rotation around the body's Z axis.</summary>
    FreezeRotationZ = AxisLocks.AngularZ,

    /// <summary>No movement.</summary>
    FreezePosition = FreezePositionX | FreezePositionY | FreezePositionZ,

    /// <summary>No rotation.</summary>
    FreezeRotation = FreezeRotationX | FreezeRotationY | FreezeRotationZ,

    /// <summary>Neither movement nor rotation.</summary>
    FreezeAll = FreezePosition | FreezeRotation,
}

/// <summary>Whether a query reports trigger colliders.</summary>
public enum QueryTriggerInteraction
{
    /// <summary>Uses <see cref="PhysicsSettings.QueriesHitTriggers"/>.</summary>
    UseGlobal,

    /// <summary>Never reports triggers.</summary>
    Ignore,

    /// <summary>Always reports triggers.</summary>
    Collide,
}
