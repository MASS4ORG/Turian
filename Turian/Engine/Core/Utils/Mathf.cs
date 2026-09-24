namespace Turian.Engine.Core;

/// <summary>
/// General purpose Vector and Quaternion operations that are not
/// present in <see cref="System.Numerics"/> by default.
/// </summary>
public static class Mathf
{
    /// <summary>
    /// Gets the up vector (0, 1, 0).
    /// </summary>
    public static Vector3 Up => new(0.0f, 1.0f, 0.0f);

    /// <summary>
    /// Gets the down vector (0, -1, 0).
    /// </summary>
    public static Vector3 Down => new(0.0f, -1.0f, 0.0f);

    /// <summary>
    /// Gets the right vector (1, 0, 0).
    /// </summary>
    public static Vector3 Right => new(1.0f, 0.0f, 0.0f);

    /// <summary>
    /// Gets the left vector (-1, 0, 0).
    /// </summary>
    public static Vector3 Left => new(-1.0f, 0.0f, 0.0f);

    /// <summary>
    /// Gets the forward vector (0, 0, 1).
    /// </summary>
    public static Vector3 Forward => new(0.0f, 0.0f, 1.0f);

    /// <summary>
    /// Gets the backward vector (0, 0, -1).
    /// </summary>
    public static Vector3 Backward => new(0.0f, 0.0f, -1.0f);

    /// <summary>
    /// Converts radians to degrees.
    /// </summary>
    public const float RadiansToDegrees = 180f / MathF.PI;

    /// <summary>
    /// Converts degrees to radians.
    /// </summary>
    public const float DegreesToRadians = MathF.PI / 180;

    /// <summary>
    /// Converts a vector in degrees to a quaternion.
    /// </summary>
    /// <param name="vector3">The vector in degrees to convert.</param>
    /// <returns>A quaternion representing the input vector.</returns>
    public static Quaternion ToQuaternion(this Vector3 vector3)
    {
        var vector3Radians = vector3 * DegreesToRadians;
        return Quaternion.CreateFromYawPitchRoll(
            vector3Radians.Y,
            vector3Radians.X,
            vector3Radians.Z
        );
    }

    /// <summary>
    /// Unwinds an angle in degrees to the range [-180, 180].
    /// </summary>
    /// <param name="angle">The angle to unwind.</param>
    /// <returns>The unwound angle in degrees.</returns>
    public static float UnwindDegrees(float angle)
    {
        var a = angle % 360;
        return a > 180 ? a - 360f : (a < -180 ? a + 360f : a);
    }

    /// <summary>
    /// Converts a quaternion to Euler angles in degrees.
    /// </summary>
    /// <param name="quat">The quaternion to convert.</param>
    /// <returns>Euler angles in degrees (X: Pitch, Y: Yaw, Z: Roll).</returns>
    public static Vector3 ToEulerDegrees(this Quaternion quat)
    {
        Vector3 result;
        var unit =
            ((quat.W * quat.W) + (quat.X * quat.X) + (quat.Y * quat.Y) + (quat.Z * quat.Z))
            * 0.499995f;
        var test = (quat.X * quat.W) - (quat.Y * quat.Z);

        // Test singularity
        if (test >= unit)
        {
            result.Y = 2.0f * MathF.Atan2(quat.Y, quat.X);
            result.X = MathF.PI / 2;
            result.Z = 0;
        }
        else if (test <= -unit)
        {
            result.Y = -2.0f * MathF.Atan2(quat.Y, quat.X);
            result.X = -MathF.PI / 2;
            result.Z = 0;
        }
        else
        {
            var q = new Quaternion(quat.W, quat.Z, quat.X, quat.Y);

            var sinyCosp = (2.0f * q.X * q.W) + (2.0f * q.Y * q.Z);
            var cosyCosp = 1 - (2.0f * ((q.Z * q.Z) + (q.W * q.W)));
            result.Y = MathF.Atan2(sinyCosp, cosyCosp);

            var sinp = 2 * ((q.X * q.Z) - (q.W * q.Y));
            if (MathF.Abs(sinp) >= 1)
            {
                result.X = MathF.CopySign(MathF.PI / 2, sinp); // use 90 degrees if out of range
            }
            else
            {
                result.X = MathF.Asin(sinp);
            }

            var sinrCosp = (2.0f * q.X * q.Y) + (2.0f * q.Z * q.W);
            var cosrCosp = 1 - (2.0f * ((q.Y * q.Y) + (q.Z * q.Z)));
            result.Z = MathF.Atan2(sinrCosp, cosrCosp);
        }

        result *= RadiansToDegrees;
        return new Vector3(
            UnwindDegrees(result.X),
            UnwindDegrees(result.Y),
            UnwindDegrees(result.Z)
        );
    }

    /// <summary>
    /// Rotates a vector by a quaternion.
    /// </summary>
    /// <param name="me">The quaternion representing the rotation.</param>
    /// <param name="v">The vector to rotate.</param>
    /// <returns>The rotated vector.</returns>
    public static Vector3 Rotate(this Quaternion me, Vector3 v)
    {
        var qv = new Quaternion(v.X, v.Y, v.Z, 0);
        var result = me * qv * Quaternion.Conjugate(me);
        return new(result.X, result.Y, result.Z);
    }
}
