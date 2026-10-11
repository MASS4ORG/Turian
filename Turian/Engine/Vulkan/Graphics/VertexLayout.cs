namespace Turian.Engine.Core;

/// <summary>Describes the <see cref="Vertex"/> format to Vulkan pipelines.</summary>
public static class VertexLayout
{
    extension(Vertex)
    {
        /// <summary>
        /// Gets an array of vertex input binding descriptions for this vertex format.
        /// </summary>
        /// <returns>An array of <see cref="VertexInputBindingDescription"/> objects.</returns>
        public static VertexInputBindingDescription[] GetBindingDescriptions()
        {
            VertexInputBindingDescription[] bindingDescriptions =
            [
                new VertexInputBindingDescription()
                {
                    Binding = 0,
                    Stride = Vertex.SizeOf(),
                    InputRate = VertexInputRate.Vertex,
                }
            ];

            return bindingDescriptions;
        }

        /// <summary>
        /// Gets an array of vertex input attribute descriptions for this vertex format.
        /// </summary>
        /// <returns>An array of <see cref="VertexInputAttributeDescription"/> objects.</returns>
        public static VertexInputAttributeDescription[] GetAttributeDescriptions()
        {
            return [
                new VertexInputAttributeDescription()
                {
                    Binding = 0,
                    Location = 0,
                    Format = Format.R32G32B32Sfloat,
                    Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position)),
                },
                new VertexInputAttributeDescription()
                {
                    Binding = 0,
                    Location = 1,
                    Format = Format.R32G32B32Sfloat,
                    Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Color)),
                },
                new VertexInputAttributeDescription()
                {
                    Binding = 0,
                    Location = 2,
                    Format = Format.R32G32B32Sfloat,
                    Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Normal)),
                },
                new VertexInputAttributeDescription()
                {
                    Binding = 0,
                    Location = 3,
                    Format = Format.R32G32Sfloat,
                    Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Uv)),
                },
                new VertexInputAttributeDescription()
                {
                    Binding = 0,
                    Location = 4,
                    Format = Format.R32G32B32A32Sfloat,
                    Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Tangent)),
                }
            ];
        }
    }
}
