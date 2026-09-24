namespace Turian.Engine.Core;

/// <summary>
/// A world-space ray: a starting point and a normalized direction.
/// </summary>
/// <param name="Origin">The point the ray starts from.</param>
/// <param name="Direction">The ray's direction. Expected to be normalized.</param>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    /// <summary>Returns the point at <paramref name="distance"/> along the ray.</summary>
    /// <param name="distance">Distance from <see cref="Origin"/>, along <see cref="Direction"/>.</param>
    public Vector3 GetPoint(float distance) => Origin + (Direction * distance);
}
