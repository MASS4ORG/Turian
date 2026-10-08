using Turian.Editor.Obj;

namespace Turian.Tests;

/// <summary>Checks asymmetric imported geometry and transforms against the camera's world-up convention.</summary>
public sealed class ModelOrientationTests
{
    /// <summary>An OBJ authored above its origin remains above it in both geometry and normals.</summary>
    [Fact]
    public void ObjPreservesUpwardGeometryAndNormals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"turian-up-{Guid.NewGuid():N}.obj");
        try
        {
            File.WriteAllText(path, """
                v 0 1 0
                v 1 1 0
                v 0 2 0
                vt 0 0
                vt 1 0
                vt 0 1
                vn 0 1 0
                f 1/1/1 2/2/1 3/3/1
                """);
            var directory = Directory.CreateTempSubdirectory("turian-orientation-");
            ModelBuilder builder;
            try
            {
                var importer = new ObjModelImporter();
                var artifact = Assert.Single(importer.ImportToCache(importer.CreateAsset(path), path, directory.FullName));
                builder = GltfModelReader.Load(Path.Combine(directory.FullName, artifact));
            }
            finally { directory.Delete(recursive: true); }
            Assert.All(builder.Vertices, vertex =>
            {
                Assert.True(vertex.Position.Y > 0);
                Assert.Equal(Vector3.UnitY, vertex.Normal);
            });
            var camera = new EditorCamera { Position = new Vector3(0, 0, -5) };
            Assert.All(builder.Vertices, vertex => Assert.True(camera.Project(vertex.Position).Y < 0));
            Assert.Equal(4, new ObjModelImporter().Version);
        }
        finally { File.Delete(path); }
    }

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
