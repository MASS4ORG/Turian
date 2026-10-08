namespace Turian.Engine.Core;

/// <summary>
/// Utility class for loading and creating 3D models.
/// </summary>
public static partial class ModelUtils
{
    /// <summary>
    /// Loads the content of an embedded resource as a string.
    /// </summary>
    /// <param name="path">The path to the embedded resource.</param>
    /// <param name="type">The type within the same assembly that contains the embedded resource.</param>
    /// <returns>The content of the embedded resource as a string.</returns>
    public static string LoadEmbeddedResource(string path, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        using var s = type.Assembly.GetManifestResourceStream(path);
        if (s is null)
        {
            return string.Empty;
        }

        using var sr = new StreamReader(s);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Gets the text content of an embedded resource with the specified filename.
    /// </summary>
    /// <param name="filename">The filename of the embedded resource.</param>
    /// <returns>The text content of the embedded resource.</returns>
    public static string GetEmbeddedResourceObjText(string filename)
    {
        var assembly = Assembly.GetExecutingAssembly();
        // foreach (var item in assembly.GetManifestResourceNames()) { }
        var resourceName =
            assembly.GetManifestResourceNames().FirstOrDefault(s => s.EndsWith(filename, StringComparison.InvariantCultureIgnoreCase))
            ?? throw new FileNotFoundException(
                $"*** No obj file found with name {filename}\n*** Check that resourceName and try again!  Did you forget to set obj file to Embedded Resource/Do Not Copy?"
            );
        using var stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"*** No shader file found at {resourceName}\n*** Check that resourceName and try again!  Did you forget to set glsl file to Embedded Resource/Do Not Copy?"
            );
        using var reader = new StreamReader(stream);
        var result = reader.ReadToEnd();
        return result;
    }

    /// <summary>
    /// Reads the JSON chunk from a GLB (binary glTF) file without decoding the binary buffer.
    /// </summary>
    public static string LoadJsonFromGlb(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 20)
            {
                throw new InvalidOperationException("GLB file too small");
            }

            // GLB header: magic (4 bytes: "glTF"), version (4 bytes), length (4 bytes)
            var magic = Encoding.ASCII.GetString(data, 0, 4);
            if (magic != "glTF")
            {
                throw new InvalidOperationException("Invalid GLB magic number");
            }

            var version = BitConverter.ToUInt32(data, 4);
            if (version != 2)
            {
                throw new InvalidOperationException($"Unsupported GLB version: {version}");
            }

            // GLB 12-byte header is followed by chunks. First chunk is always JSON.
            // Chunk header layout: length (4 bytes) + type (4 bytes), then data.
            var jsonChunkLength = BitConverter.ToUInt32(data, 12);
            var jsonChunkType = Encoding.ASCII.GetString(data, 16, 4);
            if (jsonChunkType != "JSON")
            {
                throw new InvalidOperationException("First GLB chunk is not JSON");
            }

            var jsonStart = 20;
            var json = Encoding.UTF8.GetString(data, jsonStart, (int)jsonChunkLength);
            return json;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse GLB file at {path}", ex);
        }
    }

    static byte[] LoadBinaryChunkFromGlb(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 20)
            {
                return [];
            }

            // 12-byte GLB header, then JSON chunk header (length + type) at offset 12.
            var jsonChunkLength = BitConverter.ToUInt32(data, 12);
            var jsonChunkStart = 20;
            var binChunkStart = (int)(jsonChunkStart + jsonChunkLength);

            if (data.Length < binChunkStart + 8)
            {
                return [];
            }

            // Read BIN chunk size and type
            var binChunkLength = BitConverter.ToUInt32(data, binChunkStart);
            var binChunkType = Encoding.ASCII.GetString(data, binChunkStart + 4, 4);
            if (binChunkType != "BIN\0")
            {
                return [];
            }

            var binDataStart = binChunkStart + 8;
            var result = new byte[(int)binChunkLength];
            Array.Copy(data, binDataStart, result, 0, (int)binChunkLength);
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Reads a glTF 2.0 file (<c>.gltf</c> or <c>.glb</c>) into a <see cref="ModelBuilder"/>.
    /// </summary>
    /// <param name="path">The path to the glTF file.</param>
    public static ModelBuilder LoadGltfToBuilder(string path) => GltfModelReader.Load(path);

    internal static ModelBuilder ParseGltf(string json, byte[]? embeddedBinary, string? basePath = null) =>
        GltfModelReader.ReadJson(json, embeddedBinary, (_, uri) =>
            File.ReadAllBytes(Path.Combine(basePath ?? string.Empty, Uri.UnescapeDataString(uri))));

    /// <summary>
    /// Reads the encoded bytes (PNG, JPEG, …) of an image a glTF file embeds, either in a buffer
    /// view or as a <c>data:</c> URI.
    /// </summary>
    /// <param name="path">The path to the glTF file.</param>
    /// <param name="imageIndex">Index into the file's <c>images</c> array.</param>
    /// <returns>The image bytes, or <c>null</c> when the image is an external file or does not exist.</returns>
    public static byte[]? LoadGltfEmbeddedImage(string path, int imageIndex)
    {
        var isGlb = Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(isGlb ? LoadJsonFromGlb(path) : File.ReadAllText(path));
        var root = doc.RootElement;

        if (!root.TryGetProperty("images", out var images) || imageIndex < 0 || imageIndex >= images.GetArrayLength())
            return null;

        var image = images[imageIndex];
        if (image.TryGetProperty("uri", out var uriElem))
        {
            var uri = uriElem.GetString() ?? string.Empty;
            return uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? DecodeDataUri(uri) : null;
        }

        if (!image.TryGetProperty("bufferView", out var viewIndex) || !root.TryGetProperty("bufferViews", out var views))
            return null;

        var view = views[viewIndex.GetInt32()];
        var buffers = LoadGltfBuffers(root, isGlb ? LoadBinaryChunkFromGlb(path) : null, Path.GetDirectoryName(path));
        var buffer = buffers[view.GetProperty("buffer").GetInt32()];
        var offset = view.TryGetProperty("byteOffset", out var offsetElem) ? offsetElem.GetInt32() : 0;
        return [.. buffer.AsSpan(offset, view.GetProperty("byteLength").GetInt32())];
    }

    static byte[] DecodeDataUri(string uri) =>
        Convert.FromBase64String(uri[(uri.IndexOf(',', StringComparison.Ordinal) + 1)..]);

    static List<byte[]> LoadGltfBuffers(JsonElement root, byte[]? embeddedBinary, string? basePath)
    {
        var buffers = new List<byte[]>();
        if (!root.TryGetProperty("buffers", out var buffersElem)) return buffers;

        foreach (var buf in buffersElem.EnumerateArray())
        {
            if (buf.TryGetProperty("uri", out var uriElem))
            {
                var uri = uriElem.GetString() ?? string.Empty;
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    buffers.Add(DecodeDataUri(uri));
                }
                else
                {
                    buffers.Add(File.ReadAllBytes(Path.Combine(basePath ?? string.Empty, uri)));
                }
            }
            else
            {
                buffers.Add(embeddedBinary ?? []);
            }
        }

        return buffers;
    }

    /// <summary>
    /// Creates a 3D cube model with 6 faces.
    /// </summary>
    /// <param name="vulkan">The Vulkan instance to use for rendering.</param>
    /// <returns>The created 3D cube model.</returns>
    public static Model CreateCubeModel6(Vulkan vulkan)
    {
        var h = .5f;
        var builder = new ModelBuilder
        {
            Vertices =
            [
                // left face (white)
                new(new(-h, -h, -h), Color.White.Rgb),
                new(new(-h, h, h), Color.White.Rgb),
                new(new(-h, -h, h), Color.White.Rgb),
                new(new(-h, h, -h), Color.White.Rgb),
                // x+ right face (red)
                new(new(h, -h, -h), Color.Red.Rgb),
                new(new(h, h, h), Color.Red.Rgb),
                new(new(h, -h, h), Color.Red.Rgb),
                new(new(h, h, -h), Color.Red.Rgb),
                // y+ top face (green, remember y axis points down)
                new(new(-h, -h, -h), Color.Green.Rgb),
                new(new(h, -h, h), Color.Green.Rgb),
                new(new(-h, -h, h), Color.Green.Rgb),
                new(new(h, -h, -h), Color.Green.Rgb),
                // bottom face (cyan)
                new(new(-h, h, -h), Color.Cyan.Rgb),
                new(new(h, h, h), Color.Cyan.Rgb),
                new(new(-h, h, h), Color.Cyan.Rgb),
                new(new(h, h, -h), Color.Cyan.Rgb),
                // z+ nose face (blue)
                new(new(-h, -h, h), Color.Blue.Rgb),
                new(new(h, h, h), Color.Blue.Rgb),
                new(new(-h, h, h), Color.Blue.Rgb),
                new(new(h, -h, h), Color.Blue.Rgb),
                // tail face (orange)
                new(new(-h, -h, -h), Color.Orange.Rgb),
                new(new(h, h, -h), Color.Orange.Rgb),
                new(new(-h, h, -h), Color.Orange.Rgb),
                new(new(h, -h, -h), Color.Orange.Rgb),
            ],
            Indices =
            [
                0,
                1,
                2,
                0,
                3,
                1,
                4,
                5,
                6,
                4,
                7,
                5,
                8,
                9,
                10,
                8,
                11,
                9,
                12,
                13,
                14,
                12,
                15,
                13,
                16,
                17,
                18,
                16,
                19,
                17,
                20,
                21,
                22,
                20,
                23,
                21
            ]
        };

        return new Model(vulkan, builder);
    }

}
