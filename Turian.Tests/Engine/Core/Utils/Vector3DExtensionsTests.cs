namespace Turian.Tests;

/// <summary>
/// Test Transform operations
/// </summary>
public class Vector3DExtensionsTests
{
    /// <summary>
    /// The quaternion dimension value when the euler angle is 90 degrees
    /// </summary>
    public const float Quarternion90 = 0.7071f;

    /// <summary>
    /// Convert Quaternion to Euler degrees Vector3D
    /// </summary>
    [Fact]
    public void QuaternionToEulerDegrees()
    {
        var quat = Quaternion.Identity;
        var quartEuler = quat.ToEulerDegrees();

        Assert.Equal(Vector3.Zero, quartEuler);
    }

    /// <summary>
    /// Check if the conjugate function is performing correctly
    /// </summary>
    [Fact]
    public void QuaternionConjugateReturnsExpectedConjugate()
    {
        // Arrange
        var quaternion = new Quaternion(1, 2, 3, 4);
        var expectedConjugate = new Quaternion(-1, -2, -3, 4);

        // Act
        var actualConjugate = Quaternion.Conjugate(quaternion);

        // Assert
        Assert.Equal(expectedConjugate, actualConjugate);
    }

    /// <summary>
    /// Convert Euler degrees in Vector3D to Quaternion and back, cosiders special
    /// cases with gimbal lock effect
    /// </summary>
    /// <param name="angleX"></param>
    /// <param name="angleY"></param>
    /// <param name="angleZ"></param>
    /// <param name="gimbalX"></param>
    /// <param name="gimbalY"></param>
    /// <param name="gimbalZ"></param>
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(90f, 0f, 0f)]
    [InlineData(0f, 90f, 0f)]
    [InlineData(0f, 0f, 90f)]
    [InlineData(30f, 60f, 90f)]
    [InlineData(90f, 90f, 90f, 90f, 0f, 0f)] // gimbal lock?
    [InlineData(180f, 180f, 180f, 0f, 0f, 0f)] // gimbal lock?
    [InlineData(270f, 270f, 270f, -90f, -180f, 0f)] // gimbal lock?
    [InlineData(360f, 360f, 360f)]
    [InlineData(720f, 720f, 720f)]
    [InlineData(-90, 0, 0)]
    [InlineData(0, -90, 0)]
    [InlineData(0, 0, -90)]
    [InlineData(-30, -60, -90)]
    [InlineData(-90, -90, -90, -90f, 180f, 0f)] // gimbal lock?
    [InlineData(-180, -180, -180, 0f, 0f, 0f)] // gimbal lock?
    [InlineData(-270, -270, -270, 90f, 0f, 0f)] // gimbal lock?
    [InlineData(-360, -360, -360)]
    [InlineData(-720, -720, -720)]
    public void TestEulerToQuaternionToEuler2(
        float angleX,
        float angleY,
        float angleZ,
        float? gimbalX = null,
        float? gimbalY = null,
        float? gimbalZ = null
    )
    {
        var originalEuler = new Vector3(angleX, angleY, angleZ);
        var expectedEuler =
            (gimbalX is { } x && gimbalY is { } y && gimbalZ is { } z)
                ? new Vector3(
                    Mathf.UnwindDegrees(x),
                    Mathf.UnwindDegrees(y),
                    Mathf.UnwindDegrees(z)
                )
                : new Vector3(
                    Mathf.UnwindDegrees(angleX),
                    Mathf.UnwindDegrees(angleY),
                    Mathf.UnwindDegrees(angleZ)
                );

        var result = originalEuler.ToQuaternion().ToEulerDegrees();

        Assert.Equal((double)expectedEuler.X, result.X, 3);
        Assert.Equal((double)expectedEuler.Y, result.Y, 3);
        Assert.Equal((double)expectedEuler.Z, result.Z, 3);
    }

    /// <summary>
    /// Convert Euler angles in Vector3D to Quaternion
    /// </summary>
    /// <param name="angleX"></param>
    /// <param name="angleY"></param>
    /// <param name="angleZ"></param>
    /// <param name="qX"></param>
    /// <param name="qY"></param>
    /// <param name="qZ"></param>
    /// <param name="qW"></param>
    [Theory]
    [InlineData(90, 0, 0, 1, 0, 0, 1)]
    [InlineData(0, 90, 0, 0, 1, 0, 1)]
    [InlineData(0, 0, 90, 0, 0, 1, 1)]
    public void Vector3DToEulerToQuaternions(
        float angleX,
        float angleY,
        float angleZ,
        float qX,
        float qY,
        float qZ,
        float qW
    )
    {
        var angles = new Vector3(angleX, angleY, angleZ);
        var expected = new Quaternion(qX, qY, qZ, qW) * Quarternion90;
        var quaternion = angles.ToQuaternion();

        Assert.Equal((double)expected.X, quaternion.X, 3);
        Assert.Equal((double)expected.Y, quaternion.Y, 3);
        Assert.Equal((double)expected.Z, quaternion.Z, 3);
        Assert.Equal((double)expected.W, quaternion.W, 3);
    }

    /// <summary>
    /// Rotate a Vector3D given a Quaternion
    /// </summary>
    [Fact]
    public void QuaternionRotateVector()
    {
        var euler = new Vector3(45, 45, 45);
        var quaternion = euler.ToQuaternion();
        var vector = new Vector3(1, 0, 0);

        // Perform rotation
        var rotatedVector = quaternion.Rotate(vector);

        // Compare with expected rotated vector
        var expectedVector = new Vector3(0.854f, 0.5f, -0.146f);
        Assert.Equal((double)expectedVector.X, rotatedVector.X, 3);
        Assert.Equal((double)expectedVector.Y, rotatedVector.Y, 3);
        Assert.Equal((double)expectedVector.Z, rotatedVector.Z, 3);
    }

    /// <summary>
    /// Rotate a Vector3D given a Quaternion
    /// </summary>
    [Fact]
    public void QuaternionRotateVectorFromIdentity()
    {
        // Arrange
        var rotation = Quaternion.Identity;
        var original = new Vector3(90, 0, 0);

        // Act
        var rotated = rotation.Rotate(original);

        // Assert
        Assert.Equal(original, rotated);
    }
}
