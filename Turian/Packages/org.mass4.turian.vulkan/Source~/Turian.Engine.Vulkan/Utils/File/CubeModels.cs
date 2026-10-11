namespace Turian.Engine.Core;

/// <summary>Builds small colored cube models on a Vulkan device, for tests and diagnostics.</summary>
public static class CubeModels
{
    /// <summary>
    /// Creates a 3D cube model with 3 faces (and 3 missing).
    /// </summary>
    /// <param name="vulkan">The Vulkan instance to use for rendering.</param>
    /// <returns>The created 3D cube model.</returns>
    public static VulkanModel CreateCubeModel3(Vulkan vulkan)
    {
        var h = .5f;
        var builder = new ModelBuilder
        {
            Vertices =
            [
                // x+ right face (red)
                new(new(h, -h, -h), Color.Red.Rgb),
                new(new(h, h, h), Color.Red.Rgb),
                new(new(h, -h, h), Color.Red.Rgb),
                new(new(h, h, -h), Color.Red.Rgb),
                // y+ top face (green, remember y axis points down)
                new(new(-h, h, -h), Color.Green.Rgb),
                new(new(h, h, h), Color.Green.Rgb),
                new(new(-h, h, h), Color.Green.Rgb),
                new(new(h, h, -h), Color.Green.Rgb),
                // z+ nose face (blue)
                new(new(-h, -h, h), Color.Blue.Rgb),
                new(new(h, h, h), Color.Blue.Rgb),
                new(new(-h, h, h), Color.Blue.Rgb),
                new(new(h, -h, h), Color.Blue.Rgb),
            ],
            Indices = [0, 1, 2, 0, 3, 1, 4, 5, 6, 4, 7, 5, 8, 9, 10, 8, 11, 9]
        };

        return new VulkanModel(vulkan, builder);
    }

    /// <summary>
    /// Creates a 3D cube model with 6 faces.
    /// </summary>
    /// <param name="vulkan">The Vulkan instance to use for rendering.</param>
    /// <returns>The created 3D cube model.</returns>
    public static VulkanModel CreateCubeModel6(Vulkan vulkan)
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

        return new VulkanModel(vulkan, builder);
    }
}
