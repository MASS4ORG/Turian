namespace Turian.Engine.Core;

/// <summary>
/// Configuration information for creating a graphics pipeline.
/// </summary>
public struct PipelineConfigInfo
{
    /// <summary>
    /// An array of binding descriptions for vertex input.
    /// </summary>
    public VertexInputBindingDescription[] BindingDescriptions;

    /// <summary>
    /// An array of attribute descriptions for vertex input.
    /// </summary>
    public VertexInputAttributeDescription[] AttributeDescriptions;

    /// <summary>
    /// Configuration information for viewport state.
    /// </summary>
    public PipelineViewportStateCreateInfo ViewportInfo;

    /// <summary>
    /// Configuration information for input assembly state.
    /// </summary>
    public PipelineInputAssemblyStateCreateInfo InputAssemblyInfo;

    /// <summary>
    /// Configuration information for rasterization state.
    /// </summary>
    public PipelineRasterizationStateCreateInfo RasterizationInfo;

    /// <summary>
    /// Configuration information for multisample state.
    /// </summary>
    public PipelineMultisampleStateCreateInfo MultisampleInfo;

    /// <summary>
    /// Configuration for the color blend attachment state.
    /// </summary>
    public PipelineColorBlendAttachmentState ColorBlendAttachment;

    /// <summary>
    /// Configuration information for color blend state.
    /// </summary>
    public PipelineColorBlendStateCreateInfo ColorBlendInfo;

    /// <summary>
    /// Configuration information for depth and stencil state.
    /// </summary>
    public PipelineDepthStencilStateCreateInfo DepthStencilInfo;

    /// <summary>
    /// The pipeline layout to be used.
    /// </summary>
    public PipelineLayout PipelineLayout; // no default to be set

    /// <summary>
    /// The render pass to be used.
    /// </summary>
    public RenderPass RenderPass; // no default to be set

    /// <summary>
    /// The subpass index.
    /// </summary>
    public uint Subpass;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineConfigInfo"/> struct with default values.
    /// </summary>
    public PipelineConfigInfo()
    {
        Subpass = 0;
        BindingDescriptions = [];
        AttributeDescriptions = [];
    }
}
