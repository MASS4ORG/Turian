namespace Turian.Editor.Core;

/// <summary>
/// Parses a Wavefront OBJ file into a <see cref="ModelBuilder"/>. OBJ import is editor-only: the
/// runtime asset path bakes geometry into a <c>.ammesh</c> blob (see <see cref="ModelAssetImporter"/>)
/// and never parses OBJ itself, so <c>JeremyAnsel.Media.WavefrontObj</c> is an editor dependency only.
/// </summary>
public static class ObjModelBuilder
{
    /// <summary>Loads a 3D model from a Wavefront OBJ file and converts it into vertices and indices.</summary>
    /// <param name="path">The path to the Wavefront OBJ file to load.</param>
    public static ModelBuilder Load(string path)
    {
        var objFile = ObjFile.FromFile(path);

        var vertexMap = new Dictionary<Vertex, uint>();
        var vertices = new List<Vertex>();
        var indices = new List<uint>();

        foreach (var face in objFile.Faces)
        {
            foreach (var vFace in face.Vertices)
            {
                var vertexIndex = vFace.Vertex;
                var vertex = objFile.Vertices[vertexIndex - 1];
                var positionOut = new Vector3(
                    vertex.Position.X,
                    -vertex.Position.Y,
                    vertex.Position.Z
                );

                Vector3 colorOut;
                if (vertex.Color is not null)
                {
                    colorOut = new(
                        vertex.Color.Value.X,
                        vertex.Color.Value.Y,
                        vertex.Color.Value.Z
                    );
                }
                else
                {
                    colorOut = new(1f, 1f, 1f);
                }

                var normalIndex = vFace.Normal;
                var normal = objFile.VertexNormals[normalIndex - 1];
                var normalOut = new Vector3(normal.X, -normal.Y, normal.Z);

                var textureIndex = vFace.Texture;
                var texture = objFile.TextureVertices[textureIndex - 1];

                // Flip Y for OBJ in Vulkan
                var textureOut = new Vector2(texture.X, -texture.Y);

                Vertex vertexOut =
                    new()
                    {
                        Position = positionOut,
                        Color = colorOut,
                        Normal = normalOut,
                        Uv = textureOut
                    };
                if (vertexMap.TryGetValue(vertexOut, out var meshIndex))
                {
                    indices.Add(meshIndex);
                }
                else
                {
                    indices.Add((uint)vertices.Count);
                    vertexMap[vertexOut] = (uint)vertices.Count;
                    vertices.Add(vertexOut);
                }
            }
        }

        var builder = new ModelBuilder
        {
            Vertices = [.. vertices],
            Indices = [.. indices]
        };

        Log.Logger.Lap(
            "Asset.obj",
            $"loaded {path}\t{builder.Vertices.Length} verts,\t{builder.Indices.Length} indices"
        );

        return builder;
    }
}
