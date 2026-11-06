[assembly: Turian.Engine.Core.ClientRuntimeHost(typeof(Turian.Engine.Core.VulkanRuntimeHost))]

namespace Turian.Engine.Core;

/// <summary>Presents a composed session through a GLFW window and Vulkan renderer.</summary>
public sealed class VulkanRuntimeHost : IClientRuntimeHost
{
    GlfwSurfaceHost? window;
    InputManager? input;
    SilkInputSource? inputSource;
    Vulkan? vulkan;
    RendererManager? renderer;
    PlayerFrames? frames;
    bool disposed;

    /// <inheritdoc/>
    public IInputSource Input => inputSource ?? throw new InvalidOperationException("Prepare the client host first.");

    /// <inheritdoc/>
    public IRenderDevice RenderDevice => vulkan ?? throw new InvalidOperationException("Prepare the client host first.");

    /// <inheritdoc/>
    public void Prepare(ClientHostContext context)
    {
        window = new GlfwSurfaceHost { ShowStats = context.Development };
        input = new InputManager(window, context.Logger);
        inputSource = new SilkInputSource(input);
        vulkan = new Vulkan(window, context.Logger, context.Settings);
    }

    /// <inheritdoc/>
    public void Initialize(GameRuntime runtime, GamePlayerContext context)
    {
        if (window is null || vulkan is null || inputSource is null || input is null)
        {
            throw new InvalidOperationException("Prepare the client host first.");
        }
        window.ShowStats = context.Development;
        window.Initialize();
        renderer = new RendererManager(window, vulkan, runtime.Assets, context.Logger);
        renderer.Initialize();
        var presenter = PlayerFrames.CreatePresenter(UiPresenters.Find(), vulkan, inputSource, runtime.Services);
        frames = new PlayerFrames(runtime, presenter,
            () => window.FramebufferSize,
            texture => renderer.OverlayTexture = texture,
            (deltaTime, camera, root) =>
            {
                renderer.Render(deltaTime, camera, root);
                window.CullingStats = renderer.CullingStats;
            }, window.Close);
        window.Resized += size => frames.Resize(size.X, size.Y);
        window.SurfaceLost += () => frames.Paused = true;
        window.SurfaceRestored += () => frames.Paused = false;
        input.OnKeyPressed += PlayerFrames.KeyPressed;
        window.Title = runtime.ProductName;
        window.SetIcon(runtime.Content, PlayerSettings.BuiltIconFileName);
        frames.Resize(window.FramebufferSize.X, window.FramebufferSize.Y);
    }

    /// <inheritdoc/>
    public int Run(GameRuntime runtime)
    {
        if (input is null || inputSource is null || vulkan is null || window is null || frames is null)
        {
            throw new InvalidOperationException("Prepare the client host first.");
        }
        input.Initalize();
        inputSource.Initialize();
        frames.Resize(window.FramebufferSize.X, window.FramebufferSize.Y);
        vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);
        window.Run(frames.Frame);
        vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);
        return Application.ExitCode;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        frames?.Dispose();
        renderer?.Dispose();
        inputSource?.Dispose();
        input?.Dispose();
        vulkan?.Device?.Dispose();
        window?.Dispose();
    }
}
