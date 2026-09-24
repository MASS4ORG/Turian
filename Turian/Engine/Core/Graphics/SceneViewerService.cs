namespace Turian.Engine.Core;

/// <summary>
/// Per-viewer rendering service for the editor's Scene View panel.
/// One instance per open panel. All instances share the process-wide <see cref="Vulkan"/> device.
///
/// Ownership model:
///   - Shared (singleton): <see cref="Vulkan"/> (VkInstance, VkDevice, queues)
///   - Per-viewer (owned here): <see cref="OffscreenFrameTarget"/>, render systems,
///     descriptor resources, UBO buffers, <see cref="EditorCamera"/>
///
/// The editor overlay root — for future overlays (grid, sky, etc.) — is a separate
/// <see cref="Node"/> tree owned here, never part of any SceneManager scene, never serialized.
/// </summary>
public sealed class SceneViewerService : IDisposable
{
    /// <summary>The editor camera for this viewer. Not a scene node — never serialized.</summary>
    public EditorCamera Camera { get; }

    /// <summary>Current render width.</summary>
    public uint Width => frameTarget.Width;

    /// <summary>Current render height.</summary>
    public uint Height => frameTarget.Height;

    /// <summary>
    /// The editor-only overlay scene root. Add editor-only nodes here (grid, sky, etc.).
    /// Never shown in the SceneTree panel. Never passed to SceneManager. Never serialized.
    /// </summary>
    public Node EditorOverlayRoot { get; } = new Node { Name = "__EditorOverlay" };

    /// <summary>
    /// The immediate-mode gizmo API exposed to draw calls during <see cref="Render"/>.
    /// The buffer is cleared automatically at the start of each frame.
    /// </summary>
    public Gizmos Gizmos { get; } = new();

    /// <summary>
    /// Called after the gizmo lists are cleared and before the gizmo render passes consume them.
    /// Populate <see cref="Gizmos"/> here.
    /// </summary>
    public Action<Gizmos>? OnPopulateGizmos { get; set; }

    readonly Vulkan vulkan;
    OffscreenFrameTarget frameTarget;
    DescriptorPool globalPool = null!;
    DescriptorSetLayout globalSetLayout = null!;
    DescriptorSet globalDescriptorSet;
    GlobalUbo ubo = null!;
    Buffer uboBuffer = null!;
    Buffer sboBuffer = null!;
    SboMeshTest sboMeshTest = default;
    IRenderSystem standardSystem = null!;
    IRenderSystem meshSystem = null!;
    GizmoRenderSystem gizmoSystem = null!;
    WorldUiRenderSystem worldUiSystem = null!;
    OverlayRenderSystem overlayUiSystem = null!;

    /// <summary>
    /// A texture composited over the rendered frame, after the scene and gizmos — the engine's
    /// screen-space GUI, or <c>null</c> for none. The caller owns the texture's lifetime.
    /// </summary>
    public Texture? OverlayTexture
    {
        get => overlayUiSystem.Source;
        set => overlayUiSystem.Source = value;
    }

    /// <summary>
    /// Produces the overlay texture for a frame from the target size and delta time, called once
    /// per <see cref="Render"/> before the pass. The screen-space GUI is wired here; leave it
    /// <c>null</c> to composite nothing. Whatever it returns is assigned to <see cref="OverlayTexture"/>.
    /// </summary>
    public Func<uint, uint, float, Texture?>? OverlaySource { get; set; }

    /// <summary>
    /// Produces the world-space UI panels to draw this frame — each a rasterized texture on a unit
    /// quad placed by a model matrix. Called once per <see cref="Render"/> before the pass. Leave it
    /// <c>null</c> to draw no world-space UI.
    /// </summary>
    public Func<WorldUiFrame, IReadOnlyList<WorldUiQuad>>? WorldUiSource { get; set; }

    /// <summary>
    /// Creates a new scene viewer service.
    /// </summary>
    /// <param name="vulkan">The shared headless Vulkan context (editor singleton).</param>
    /// <param name="initialWidth">Initial viewport width in pixels.</param>
    /// <param name="initialHeight">Initial viewport height in pixels.</param>
    public SceneViewerService(Vulkan vulkan, uint initialWidth, uint initialHeight)
    {
        ArgumentNullException.ThrowIfNull(vulkan);
        this.vulkan = vulkan;
        Camera = new EditorCamera();
        Camera.Resize(initialWidth, initialHeight);
        frameTarget = new OffscreenFrameTarget(vulkan.Vk, vulkan.Device, initialWidth, initialHeight);
        InitializeResources();
    }

    /// <summary>
    /// Renders one frame of <paramref name="sceneRoot"/> into the offscreen target.
    /// Call <see cref="CopyPixels"/> afterwards to read the result.
    /// </summary>
    /// <param name="sceneRoot">The root of the hierarchy to render.</param>
    /// <param name="deltaTime">The time elapsed since the previous frame, in seconds.</param>
    /// <param name="camera">
    /// The camera to render from. Defaults to this viewer's <see cref="Camera"/>; the Game panel
    /// passes the running scene's <see cref="CameraComponent"/> instead. The caller owns resizing
    /// any camera it supplies.
    /// </param>
    public void Render(Node sceneRoot, double deltaTime, ICamera? camera = null)
    {
        ArgumentNullException.ThrowIfNull(sceneRoot);

        var activeCamera = camera ?? Camera;

        Gizmos.Clear();
        OnPopulateGizmos?.Invoke(Gizmos);

        if (OverlaySource is not null)
            OverlayTexture = OverlaySource(frameTarget.Width, frameTarget.Height, (float)deltaTime);

        worldUiSystem.Quads.Clear();
        if (WorldUiSource is not null)
        {
            worldUiSystem.Quads.AddRange(WorldUiSource(
                new WorldUiFrame(activeCamera, frameTarget.Width, frameTarget.Height, (float)deltaTime)));
        }

        var cmd = frameTarget.BeginFrame();
        if (cmd is null) return;

        ubo.Update(
            activeCamera.GetProjectionMatrix(),
            activeCamera.GetViewMatrix(),
            new Vector4(activeCamera.Front, 0));
        uboBuffer.WriteBytesToBuffer(ubo.AsBytes());

        frameTarget.BeginRenderPass(cmd.Value);

        var frameInfo = new FrameInfo
        {
            FrameIndex = 0,
            FrameTime = (float)deltaTime,
            CommandBuffer = cmd.Value,
            Camera = activeCamera,
            GlobalDescriptorSet = globalDescriptorSet,
            Nodes = sceneRoot.Children,
            ViewportWidth = frameTarget.Width,
            ViewportHeight = frameTarget.Height,
        };

        standardSystem.Render(frameInfo, ref ubo);
        meshSystem.Render(frameInfo, ref ubo);
        gizmoSystem.Render(frameInfo, ref ubo);
        worldUiSystem.Render(frameInfo, ref ubo);
        overlayUiSystem.Render(frameInfo, ref ubo);

        frameTarget.EndRenderPass(cmd.Value);
        frameTarget.EndFrame();
    }

    /// <summary>
    /// Copies the last rendered frame's BGRA8 pixels into <paramref name="destination"/>.
    /// The span must be at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public void CopyPixels(Span<byte> destination) => frameTarget.CopyToSpan(destination);

    /// <summary>
    /// Positions the editor camera to look at <paramref name="target"/> from a sensible distance,
    /// keeping the camera's current Yaw/Pitch (so the viewing angle is preserved).
    /// </summary>
    public void FrameNode(Node target, float distance = 5f)
    {
        ArgumentNullException.ThrowIfNull(target);
        Camera.Position = target.GlobalTransform.Position - Camera.Front * distance;
    }

    /// <summary>
    /// Positions the editor camera to frame the given bounds, computing distance from the
    /// camera's FOV angle. Keeps the current Yaw/Pitch so the viewing angle is preserved.
    /// </summary>
    public void FrameBounds(Bounds bounds)
    {
        if (bounds.IsEmpty) return;

        var radius = bounds.Size.Length() * 0.5f;
        if (radius < 0.001f) radius = 1f;
        var distance = radius / MathF.Tan(Camera.FieldOfView * 0.5f);
        Camera.Position = bounds.Center - Camera.Front * distance;
        Camera.FarPlane = MathF.Max(Camera.FarPlane, (distance + radius * 2f) * 1.5f);
    }

    /// <summary>
    /// Resizes the viewport. Recreates the frame target; render systems are reused (render-pass-compatible).
    /// </summary>
    public void Resize(uint width, uint height)
    {
        if (width == Width && height == Height) return;
        _ = vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);

        frameTarget.Resize(width, height);
        Camera.Resize(width, height);

        // Render systems bind to the render pass handle, which is recreated on resize.
        // Recreate them so their pipelines point to the new render pass. The overlay texture is
        // re-supplied by the caller each frame, so it is not carried across.
        standardSystem.Dispose();
        meshSystem.Dispose();
        gizmoSystem.Dispose();
        worldUiSystem.Dispose();
        overlayUiSystem.Dispose();
        InitializeRenderSystems();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _ = vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);

        standardSystem.Dispose();
        meshSystem.Dispose();
        gizmoSystem.Dispose();
        worldUiSystem.Dispose();
        overlayUiSystem.Dispose();
        globalSetLayout.Dispose();
        globalPool.Dispose();
        uboBuffer.Dispose();
        sboBuffer.Dispose();
        frameTarget.Dispose();
    }

    void InitializeResources()
    {
        InitializeDescriptorPool();
        InitializeUboBuffer();
        InitializeSboBuffer();
        InitializeGlobalSetLayout();
        InitializeRenderSystems();
    }

    void InitializeDescriptorPool()
    {
        globalPool = new DescriptorPoolBuilder(vulkan.Vk, vulkan.Device)
            .SetMaxSets(1)
            .AddPoolSize(DescriptorType.UniformBuffer, 1)
            .AddPoolSize(DescriptorType.StorageBuffer, 10)
            .Build();
    }

    void InitializeUboBuffer()
    {
        ubo = new GlobalUbo();
        uboBuffer = new Buffer(
            vulkan,
            ubo.SizeOf,
            1,
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        _ = uboBuffer.Map();
    }

    void InitializeSboBuffer()
    {
        sboBuffer = new Buffer(
            vulkan,
            SboMeshTest.SizeOf(),
            1,
            BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        _ = sboBuffer.Map();
        sboBuffer.WriteToBuffer(sboMeshTest);
    }

    void InitializeGlobalSetLayout()
    {
        var stages =
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit |
            ShaderStageFlags.MeshBitExt | ShaderStageFlags.TaskBitExt;

        globalSetLayout = new DescriptorSetLayoutBuilder(vulkan.Vk, vulkan.Device)
            .AddBinding(0, DescriptorType.UniformBuffer, stages)
            .AddBinding(1, DescriptorType.StorageBuffer, stages)
            .Build();

        _ = new DescriptorSetWriter(vulkan.Vk, vulkan.Device, globalSetLayout)
            .WriteBuffer(0, uboBuffer.DescriptorInfo())
            .WriteBuffer(1, sboBuffer.DescriptorInfo())
            .Build(globalPool, globalSetLayout.GetDescriptorSetLayout(), ref globalDescriptorSet);
    }

    void InitializeRenderSystems()
    {
        standardSystem = new StandardRenderSystem(
            vulkan, frameTarget.RenderPass, globalSetLayout.GetDescriptorSetLayout());
        meshSystem = new MeshRenderSystem(
            vulkan, frameTarget.RenderPass, globalSetLayout.GetDescriptorSetLayout());
        gizmoSystem = new GizmoRenderSystem(
            vulkan, frameTarget.RenderPass, globalSetLayout.GetDescriptorSetLayout(), Gizmos);
        worldUiSystem = new WorldUiRenderSystem(vulkan, frameTarget.RenderPass, globalSetLayout);
        overlayUiSystem = new OverlayRenderSystem(vulkan, frameTarget.RenderPass);
    }
}
