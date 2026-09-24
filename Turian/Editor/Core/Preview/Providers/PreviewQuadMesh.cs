namespace Turian.Editor.Core;

/// <summary>
/// A shared unit quad, facing -Z, used as the display surface for the material preview swatch. Built
/// on demand and cached per Vulkan device — there is no asset behind it, so it cannot go through the
/// normal <c>AssetReference</c>-bound model pipeline.
/// </summary>
public static class PreviewQuadMesh
{
    static readonly ConcurrentDictionary<nint, Model> cache = new();

    /// <summary>Gets the shared unit quad model for <paramref name="vulkan"/>, building it if needed.</summary>
    public static Model Get(Vulkan vulkan)
    {
        ArgumentNullException.ThrowIfNull(vulkan);

        return cache.GetOrAdd(vulkan.Device.VkDevice.Handle, static (_, v) => Build(v), vulkan);
    }

    static Model Build(Vulkan vulkan)
    {
        const float h = 0.5f;
        var normal = new Vector3(0f, 0f, -1f);

        var builder = new ModelBuilder
        {
            Vertices =
            [
                new(new(-h, -h, 0f), Vector3.One) { Normal = normal, Uv = new(0f, 1f) },
                new(new(h, -h, 0f), Vector3.One) { Normal = normal, Uv = new(1f, 1f) },
                new(new(h, h, 0f), Vector3.One) { Normal = normal, Uv = new(1f, 0f) },
                new(new(-h, h, 0f), Vector3.One) { Normal = normal, Uv = new(0f, 0f) },
            ],
            Indices = [0, 1, 2, 0, 2, 3],
        };

        return new Model(vulkan, builder);
    }
}
