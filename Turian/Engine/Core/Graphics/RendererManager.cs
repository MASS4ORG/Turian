namespace Turian.Engine.Core;

/// <summary>
/// Manages rendering operations, including initializing the renderer, handling render systems, and rendering frames.
/// </summary>
/// <remarks>
/// Initializes the renderer manager with the specified window manager, Vulkan context, and logger.
/// </remarks>
/// <param name="windowManager">The window manager responsible for the rendering surface.</param>
/// <param name="vulkan">The Vulkan context used for rendering.</param>
/// <param name="logger">The logger for recording initialization steps.</param>
public class RendererManager(WindowManager windowManager, Vulkan vulkan, ILogger logger) : IDisposable
{
    // set to true to force FIFO swapping
    const bool useFifo = false;

    /// <summary>
    /// The Renderer used
    /// </summary>
    public Renderer Renderer { get; private set; } = null!;

    /// <summary>
    /// Gets or sets the delegate for custom rendering operations.
    /// </summary>
    public delegate void OnRenderDelegate(FrameInfo frameInfo, ref GlobalUbo ubo);

    /// <summary>
    /// Render solids first
    /// </summary>
    public OnRenderDelegate? OnRender { get; set; }

    DescriptorSetLayout globalSetLayout = null!;
    DescriptorSet[] globalDescriptorSets = null!;
    readonly List<IRenderSystem> renderSystems = [];
    OverlayRenderSystem overlayUiSystem = null!;
    DescriptorPool globalPool = null!;

    /// <summary>
    /// A texture composited over the frame after the scene, or <c>null</c> for none — the
    /// standalone runtime's screen-space GUI. The caller owns the texture's lifetime and
    /// re-supplies it each frame.
    /// </summary>
    public Texture? OverlayTexture
    {
        get => overlayUiSystem.Source;
        set => overlayUiSystem.Source = value;
    }
    GlobalUbo[] ubos = null!;
    Buffer[] uboBuffers = null!;
    Buffer sboMeshTestBuffer = null!;
    SboMeshTest sboMeshTest = new();
    FrameInfo frameInfo;
    readonly WindowManager windowManager = windowManager;
    readonly Vulkan vulkan = vulkan;
    readonly ILogger logger = logger;

    /// <summary>
    /// Initializes the renderer manager, including the renderer, descriptor pool, UBO buffers, and render systems.
    /// </summary>
    public void Initialize()
    {
        var frames = (uint)SwapChain.MaxFramesInFlight;
        InitializeRenderer();
        InitializeDescriptorPool(frames);
        InitializeUboBuffers(frames);
        InitializeTestBuffer();
        InitializeGlobalSetLayout(frames);
        InitializeRenderSystems();
    }

    /// <summary>
    /// Renders a frame using the specified delta time, camera, and scene node.
    /// </summary>
    /// <param name="deltaTime">The time elapsed since the last frame.</param>
    /// <param name="camera">The camera used for rendering the scene.</param>
    /// <param name="node">The root node of the scene to render.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="camera"/> or <paramref name="node"/> is null.</exception>
    public void Render(double deltaTime, ICamera camera, Node node)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(node);
        var commandBuffer = Renderer.BeginFrame();
        var frameIndex = Renderer.FrameIndex;

        if (commandBuffer is not null)
        {
            frameInfo = new()
            {
                FrameIndex = frameIndex,
                FrameTime = (float)deltaTime,
                CommandBuffer = commandBuffer.Value,
                Camera = camera,
                GlobalDescriptorSet = globalDescriptorSets[frameIndex],
                Nodes = node.Children
            };

            ubos[frameIndex].Update(
                camera.GetProjectionMatrix(),
                camera.GetViewMatrix(),
                new Vector4(camera.Front, 0)
            );
            uboBuffers[frameIndex].WriteBytesToBuffer(ubos[frameIndex].AsBytes());

            Renderer.BeginSwapChainRenderPass(commandBuffer.Value);
            if (OnRender is not null)
            {
                OnRender(frameInfo, ref ubos[frameIndex]);
            }

            Renderer.EndSwapChainRenderPass(commandBuffer.Value);

            Renderer.EndFrame();
        }
    }

    /// <summary>
    /// Disposes of resources used by the renderer manager.
    /// </summary>
    public void Dispose()
    {
        Renderer.Dispose();
        globalSetLayout.Dispose();
        globalPool.Dispose();
        sboMeshTestBuffer.Dispose();

        foreach (var item in renderSystems)
        {
            OnRender -= item.Render;
            item.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    void InitializeTestBuffer()
    {
        // this should be a Uniform Buffer, but wanted to show how Storage Buffers can work
        // with a mesh shader, you can feed in any kind of information in any format
        // there's no direct mapping/binding to vertex info, sky's the limit!
        sboMeshTestBuffer = new Buffer(
            vulkan,
            SboMeshTest.SizeOf(),
            1,
            BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
        );
        _ = sboMeshTestBuffer.Map();
        sboMeshTestBuffer.WriteToBuffer(sboMeshTest);
    }

    void InitializeGlobalSetLayout(uint frames)
    {
        var shaderuse =
            ShaderStageFlags.VertexBit
            | ShaderStageFlags.FragmentBit
            | ShaderStageFlags.MeshBitExt
            | ShaderStageFlags.TaskBitExt;
        globalSetLayout = new DescriptorSetLayoutBuilder(vulkan.Vk, vulkan.Device)
            .AddBinding(0, DescriptorType.UniformBuffer, shaderuse)
            .AddBinding(1, DescriptorType.StorageBuffer, shaderuse)
            .Build();

        globalDescriptorSets = new DescriptorSet[frames];
        for (var i = 0; i < globalDescriptorSets.Length; i++)
        {
            _ = new DescriptorSetWriter(vulkan.Vk, vulkan.Device, globalSetLayout)
                .WriteBuffer(1, sboMeshTestBuffer.DescriptorInfo())
                .WriteBuffer(0, uboBuffers[i].DescriptorInfo())
                .Build(
                    globalPool,
                    globalSetLayout.GetDescriptorSetLayout(),
                    ref globalDescriptorSets[i]
                );
        }
        logger.Lap("run", "got globalDescriptorSets");
    }

    void InitializeRenderSystems()
    {
        renderSystems.Add(
            new StandardRenderSystem(
                vulkan,
                Renderer.SwapChainRenderPass,
                globalSetLayout.GetDescriptorSetLayout()
            )
        );
        renderSystems.Add(
            new MeshRenderSystem(
                vulkan,
                Renderer.SwapChainRenderPass,
                globalSetLayout.GetDescriptorSetLayout()
            )
        );

        overlayUiSystem = new OverlayRenderSystem(vulkan, Renderer.SwapChainRenderPass);
        renderSystems.Add(overlayUiSystem);

        foreach (var item in renderSystems)
        {
            OnRender += item.Render;
        }

        logger.Lap("run", "got render systems");
    }

    void InitializeUboBuffers(uint frames)
    {
        ubos = new GlobalUbo[frames];
        uboBuffers = new Buffer[frames];
        OnChangeLights();
        logger.Lap("run", "initialized ubo buffers");
    }

    void InitializeDescriptorPool(uint frames)
    {
        globalPool = new DescriptorPoolBuilder(vulkan.Vk, vulkan.Device)
            .SetMaxSets(frames)
            .AddPoolSize(DescriptorType.UniformBuffer, frames)
            .AddPoolSize(DescriptorType.StorageBuffer, 10)
            .Build();
        logger.Lap("startup", "global descriptor pool created");
    }

    void InitializeRenderer()
    {
        Renderer = new Renderer(vulkan.Vk, vulkan.Device, windowManager.Window, useFifo);
        Renderer.Initialize();
        logger.Lap("startup", "got renderer");
    }

    void OnChangeLights()
    {
        var frames = SwapChain.MaxFramesInFlight;

        for (var i = 0; i < frames; i++)
        {
            ubos[i] = new GlobalUbo();
            uboBuffers[i] = new(
                vulkan,
                ubos[i].SizeOf,
                1,
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            );
            _ = uboBuffers[i].Map();
        }
    }
}
