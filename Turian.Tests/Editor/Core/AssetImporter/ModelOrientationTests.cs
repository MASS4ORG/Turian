namespace Turian.Tests;

/// <summary>Checks asymmetric imported transforms against the camera's world-up convention.</summary>
public sealed class ModelOrientationTests
{
    /// <summary>FBX translation and rotation preserve the source basis when converting matrix storage.</summary>
    [Fact]
    public void FbxPreservesUpwardTransforms()
    {
        var local = Matrix4x4.CreateRotationX(0.4f) * Matrix4x4.CreateTranslation(1, 2, 3);
        var method = typeof(FbxModelImporter).GetMethod("DecomposeTransform",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var (position, rotation, scale) =
            ((Vector3, Quaternion, Vector3))method.Invoke(null, [Matrix4x4.Transpose(local)])!;
        Assert.Equal(new Vector3(1, 2, 3), position);
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitY, local),
            Vector3.Transform(Vector3.UnitY, Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position))) < 1e-5f);
        Assert.Equal(3, new FbxModelImporter().Version);
    }
}
