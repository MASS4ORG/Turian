namespace Turian.Tests;

/// <summary>Verifies frustum planes against homogeneous clipping and conservative bounds intersection.</summary>
public sealed class GeometryUtilityTests
{
    /// <summary>Plane order and depth range match the six clip boundaries, including the zero-depth near plane.</summary>
    [Fact]
    public void IdentityPlanesMatchVulkanClipVolume()
    {
        var planes = new Plane[6];
        GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Identity, planes);
        Assert.Equal(new Plane(Vector3.UnitX, 1f), planes[0]);
        Assert.Equal(new Plane(-Vector3.UnitX, 1f), planes[1]);
        Assert.Equal(new Plane(Vector3.UnitY, 1f), planes[2]);
        Assert.Equal(new Plane(-Vector3.UnitY, 1f), planes[3]);
        Assert.Equal(new Plane(Vector3.UnitZ, 0f), planes[4]);
        Assert.Equal(new Plane(-Vector3.UnitZ, 1f), planes[5]);
    }

    /// <summary>A box wholly beyond any clip boundary is rejected.</summary>
    [Theory]
    [InlineData(-2f, 0f, 0.5f)]
    [InlineData(2f, 0f, 0.5f)]
    [InlineData(0f, -2f, 0.5f)]
    [InlineData(0f, 2f, 0.5f)]
    [InlineData(0f, 0f, -0.5f)]
    [InlineData(0f, 0f, 1.5f)]
    public void RejectsBoxesOutsideEachPlane(float x, float y, float z)
    {
        var planes = new Plane[6];
        GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Identity, planes);
        var center = new Vector3(x, y, z);
        Assert.False(GeometryUtility.TestPlanesAABB(planes, new Bounds(center - new Vector3(0.1f),
            center + new Vector3(0.1f))));
    }

    /// <summary>Intersecting boxes, flat bounds, boundary contacts and unknown bounds remain visible.</summary>
    [Fact]
    public void RetainsIntersectionsBoundaryContactsAndUnknownBounds()
    {
        var planes = new Plane[6];
        GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Identity, planes);
        Assert.True(GeometryUtility.TestPlanesAABB(planes, new Bounds(new Vector3(-2f), new Vector3(2f))));
        Assert.True(GeometryUtility.TestPlanesAABB(planes, new Bounds(new Vector3(1f, 0f, 0f),
            new Vector3(2f, 0f, 1f))));
        Assert.True(GeometryUtility.TestPlanesAABB(planes, default));
        Assert.True(GeometryUtility.TestPlanesAABB(planes, Bounds.Empty));
        Assert.True(GeometryUtility.TestPlanesAABB(planes, new Bounds(new Vector3(float.NaN), new Vector3(float.NaN))));
        Assert.True(GeometryUtility.TestPlanesAABB([], new Bounds(new Vector3(100f), new Vector3(101f))));
    }

    /// <summary>Camera planes agree with direct clip-space tests through translation, yaw, pitch and roll.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraPlanesAgreeWithHomogeneousClipping(bool orthographic)
    {
        var camera = new EditorCamera
        {
            Position = new Vector3(17f, -3f, 9f),
            NearPlane = 0.3f,
            FarPlane = 25f,
            IsOrthographic = orthographic,
        };
        camera.Resize(1280, 720);
        camera.SetYawPitch(1.3f, -0.4f);
        camera.RollLocal(0.8f);
        var matrix = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        var planes = GeometryUtility.CalculateFrustumPlanes(camera);
        var random = new Random(1234);
        for (var i = 0; i < 1000; i++)
        {
            var point = camera.Position + new Vector3(random.NextSingle() * 80f - 40f,
                random.NextSingle() * 80f - 40f, random.NextSingle() * 80f - 40f);
            var clip = Vector4.Transform(new Vector4(point, 1f), matrix);
            var visible = clip.X >= -clip.W && clip.X <= clip.W && clip.Y >= -clip.W && clip.Y <= clip.W
                && clip.Z >= 0f && clip.Z <= clip.W;
            Assert.Equal(visible, GeometryUtility.TestPlanesAABB(planes, new Bounds(point, point)));
        }
    }

    /// <summary>Perspective clipping rejects behind-camera boxes and keeps geometry crossing the near plane.</summary>
    [Fact]
    public void PerspectiveHandlesNearPlaneAndCameraInsideBounds()
    {
        var camera = new EditorCamera { Position = Vector3.Zero, NearPlane = 1f, FarPlane = 10f };
        var planes = GeometryUtility.CalculateFrustumPlanes(camera);
        Assert.False(GeometryUtility.TestPlanesAABB(planes, new Bounds(new Vector3(-1f, -1f, -3f),
            new Vector3(1f, 1f, -2f))));
        Assert.True(GeometryUtility.TestPlanesAABB(planes, new Bounds(new Vector3(-0.1f, -0.1f, 0.5f),
            new Vector3(0.1f, 0.1f, 1.5f))));
        Assert.True(GeometryUtility.TestPlanesAABB(planes, new Bounds(new Vector3(-2f), new Vector3(2f))));
    }

    /// <summary>Invalid extraction inputs are rejected, and degenerate plane normals disable rejection.</summary>
    [Fact]
    public void ValidatesStorageAndToleratesDegenerateMatrices()
    {
        Assert.Throws<ArgumentNullException>(() => GeometryUtility.CalculateFrustumPlanes(null!));
        Assert.Throws<ArgumentException>(() => GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Identity, new Plane[5]));
        var planes = new Plane[6];
        GeometryUtility.CalculateFrustumPlanes(default, planes);
        Assert.True(GeometryUtility.TestPlanesAABB(planes, new Bounds(new Vector3(100f), new Vector3(101f))));
    }

    /// <summary>World bounds enclose every corner after rotation, nonuniform negative scale and translation.</summary>
    [Fact]
    public void TransformedBoundsEncloseAllCorners()
    {
        var local = new Bounds(new Vector3(-1f, -2f, -3f), new Vector3(2f, 4f, 5f));
        var matrix = Matrix4x4.CreateScale(-2f, 3f, 0.5f) * Matrix4x4.CreateRotationY(0.7f)
            * Matrix4x4.CreateTranslation(12f, -4f, 7f);
        var world = ModelBoundsUtility.ToWorldBounds(local, matrix);
        for (var corner = 0; corner < 8; corner++)
        {
            var point = Vector3.Transform(new Vector3(
                (corner & 1) == 0 ? local.Min.X : local.Max.X,
                (corner & 2) == 0 ? local.Min.Y : local.Max.Y,
                (corner & 4) == 0 ? local.Min.Z : local.Max.Z), matrix);
            Assert.InRange(point.X, world.Min.X, world.Max.X);
            Assert.InRange(point.Y, world.Min.Y, world.Max.Y);
            Assert.InRange(point.Z, world.Min.Z, world.Max.Z);
        }

        Assert.Equal(Bounds.Empty, ModelBoundsUtility.ToWorldBounds(Bounds.Empty, matrix));
    }
}
