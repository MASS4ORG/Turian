namespace Turian.Engine.Core;

/// <summary>
/// Represents the Global Uniform Buffer Object (UBO) used for global shader data.
/// </summary>
public class GlobalUbo
{
    const uint uboLights = 10;
    const uint uboDirectionalLights = 4;

    Matrix4x4 projection; // 64
    Matrix4x4 view; // 64
    Vector4 frontVec; // 16
    Vector4 ambientColor; // 16

    // size = 160

    readonly PointLight[] pointLights;

    // size = 10 * 32 = 320

    readonly DirectionalLight[] directionalLights;

    // size = 4 * 32 = 128

    /// <summary>
    /// Gets the number of point lights in the UBO.
    /// </summary>
    public int Count => pointLights.Length;

    /// <summary>
    /// Gets the number of directional light slots in the UBO.
    /// </summary>
    public int DirectionalCount => directionalLights.Length;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlobalUbo"/> class with default values.
    /// </summary>
    public GlobalUbo()
    {
        projection = Matrix4x4.Identity;
        view = Matrix4x4.Identity;
        frontVec = Vector4.UnitZ;
        ambientColor = new(1f, 1f, 1f, 0.02f);
        pointLights = new PointLight[uboLights];
        directionalLights = new DirectionalLight[uboDirectionalLights];
        ClearLights();
    }

    /// <summary>
    /// Turns every light slot off. Call this before filling the UBO from a scene: a default
    /// <see cref="PointLight"/> is white at full intensity sitting on the world origin, so slots
    /// left over from a scene with more lights would keep illuminating anything near it.
    /// </summary>
    public void ClearLights()
    {
        for (var i = 0; i < pointLights.Length; i++)
        {
            pointLights[i].SetPosition(Vector3.Zero);
            pointLights[i].SetColor(Vector4.Zero, 0f);
        }

        for (var i = 0; i < directionalLights.Length; i++)
        {
            directionalLights[i].Clear();
        }
    }

    /// <summary>
    /// Updates the UBO with new projection, view, and front vector data.
    /// </summary>
    /// <param name="projectionMatrix">The projection matrix.</param>
    /// <param name="viewMatrix">The view matrix.</param>
    /// <param name="frontVector">The front vector.</param>
    public void Update(Matrix4x4 projectionMatrix, Matrix4x4 viewMatrix, Vector4 frontVector)
    {
        projection = projectionMatrix;
        view = viewMatrix;
        frontVec = frontVector;
    }

    /// <summary>
    /// Sets the position of a point light at the specified index.
    /// </summary>
    /// <param name="lightIndex">The index of the point light.</param>
    /// <param name="position">The position to set.</param>
    public void SetPointLightPosition(int lightIndex, Vector3 position) =>
        pointLights[lightIndex].SetPosition(position);

    /// <summary>
    /// Sets the color and intensity of a point light at the specified index.
    /// </summary>
    /// <param name="lightIndex">The index of the point light.</param>
    /// <param name="color">The color to set.</param>
    /// <param name="intensity">The intensity to set.</param>
    public void SetPointLightColor(int lightIndex, Vector4 color, float intensity) =>
        pointLights[lightIndex].SetColor(color, intensity);

    /// <summary>
    /// Sets the direction, color and intensity of a directional light at the specified index.
    /// </summary>
    /// <param name="lightIndex">The index of the directional light.</param>
    /// <param name="direction">The direction the light travels in world space.</param>
    /// <param name="color">The color to set.</param>
    /// <param name="intensity">The intensity to set.</param>
    public void SetDirectionalLight(int lightIndex, Vector3 direction, Vector4 color, float intensity)
    {
        directionalLights[lightIndex].SetDirection(direction);
        directionalLights[lightIndex].SetColor(color, intensity);
    }

    /// <summary>
    /// Converts the UBO data to a byte array.
    /// </summary>
    /// <returns>A byte array containing the UBO data.</returns>
    public byte[] AsBytes()
    {
        uint offset = 0;
        uint fsize = sizeof(float);
        var vsize = fsize * 4;
        var msize = vsize * 4;
        var bytes = new byte[SizeOf];

        projection.AsBytes().CopyTo(bytes, offset);
        offset += msize;
        view.AsBytes().CopyTo(bytes, offset);
        offset += msize;

        frontVec.AsBytes().CopyTo(bytes, offset);
        offset += vsize;
        ambientColor.AsBytes().CopyTo(bytes, offset);
        offset += vsize;

        var pbytes = pointLights.AsBytes();
        pbytes.CopyTo(bytes, offset);
        offset += uboLights * PointLight.SizeOf;

        directionalLights.AsBytes().CopyTo(bytes, offset);

        return bytes;
    }

    /// <summary>
    /// Gets the size, in bytes, of the UBO.
    /// </summary>
    /// <returns>The size of the UBO in bytes.</returns>
    public uint SizeOf => 160 + (uboLights * 32) + (uboDirectionalLights * 32);
}
