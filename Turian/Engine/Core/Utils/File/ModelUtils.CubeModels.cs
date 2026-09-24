namespace Turian.Engine.Core;

public static partial class ModelUtils
{
    /// <summary>
    /// Creates a 3D cube model with 3 faces (and 3 missing).
    /// </summary>
    /// <param name="vulkan">The Vulkan instance to use for rendering.</param>
    /// <returns>The created 3D cube model.</returns>
    public static Model CreateCubeModel3(Vulkan vulkan)
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

        return new Model(vulkan, builder);
    }
}
