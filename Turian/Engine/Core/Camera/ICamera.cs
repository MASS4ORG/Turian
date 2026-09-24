namespace Turian.Engine.Core;

/// <summary>
/// Represents an interface for a camera that defines the properties and methods
/// required for controlling and manipulating the camera's view in a 3D scene.
/// </summary>
public interface ICamera
{
    /// <summary>
    /// Gets or sets the position of the camera in 3D space.
    /// </summary>
    Vector3 Position { get; set; }

    /// <summary>
    /// Gets or sets the field of view angle of the camera's projection.
    /// </summary>
    float FieldOfView { get; set; }

    /// <summary>
    /// Gets the front vector of the camera.
    /// </summary>
    Vector3 Front { get; }

    /// <summary>
    /// Gets the right vector of the camera.
    /// </summary>
    Vector3 Right { get; }

    /// <summary>
    /// Gets the up vector of the camera.
    /// </summary>
    Vector3 Up { get; }

    /// <summary>
    /// Gets the inverse view matrix of the camera.
    /// </summary>
    /// <returns>The inverse view matrix.</returns>
    Matrix4x4 GetInverseViewMatrix();

    /// <summary>
    /// Gets the projection matrix of the camera.
    /// </summary>
    /// <returns>The projection matrix.</returns>
    Matrix4x4 GetProjectionMatrix();

    /// <summary>
    /// Gets the view matrix of the camera.
    /// </summary>
    /// <returns>The view matrix.</returns>
    Matrix4x4 GetViewMatrix();

    /// <summary>
    /// Projects a 3D point in world space to a 2D point in screen space.
    /// </summary>
    /// <param name="mouse3D">The 3D point to project.</param>
    /// <returns>The projected 2D point in screen space.</returns>
    Vector2 Project(Vector3 mouse3D);

    /// <summary>
    /// Unprojects a 2D point in screen space to a 3D point in world space.
    /// </summary>
    /// <param name="mouse2D">The 2D point to unproject.</param>
    /// <returns>The unprojected 3D point in world space.</returns>
    Vector3 UnProject(Vector2 mouse2D);
}
