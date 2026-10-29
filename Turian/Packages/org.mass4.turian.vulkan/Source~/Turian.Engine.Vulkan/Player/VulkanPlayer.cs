[assembly: Turian.Engine.Core.GamePlayer(typeof(Turian.Engine.Core.VulkanPlayer))]

namespace Turian.Engine.Core;

/// <summary>
/// Plays a client build: opens a GLFW surface host, renders each loaded scene's primary camera with Vulkan, routes
/// window input to the game and composites the installed interface package's overlay. Ends its loop on
/// <see cref="Application.Quit"/>, Ctrl+C or SIGTERM.
/// </summary>
public sealed class VulkanPlayer : IGamePlayer
{
    /// <inheritdoc />
    public int Run(GamePlayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Application.BeginSession(headless: false);

        using var interrupt = Application.QuitOnInterrupt();
        var window = new GlfwSurfaceHost { ShowStats = context.Development };
        var input = new InputManager(window, context.Logger);
        var inputSource = new SilkInputSource(input);
        var runtime = GameRuntime.Start(context, inputSource, services =>
        {
            services.AddSingleton(sp => new Vulkan(window, context.Logger, sp.GetService<IAppSettings>()));
            services.AddSingleton<IRenderDevice>(sp => sp.GetRequiredService<Vulkan>());
        });
        using var dataAssets = runtime.Services.GetRequiredService<DataAssetSession>();
        var vulkan = runtime.Services.GetRequiredService<Vulkan>();
        window.Initialize();
        var renderer = new RendererManager(window, vulkan, runtime.Assets, context.Logger);
        renderer.Initialize();
        var presenter = PlayerFrames.CreatePresenter(UiPresenters.Find(), vulkan, inputSource, runtime.Locale);
        var frames = new PlayerFrames(runtime, presenter,
            () => window.FramebufferSize,
            texture => renderer.OverlayTexture = texture,
            (deltaTime, camera, root) =>
            {
                renderer.Render(deltaTime, camera, root);
                window.CullingStats = renderer.CullingStats;
            },
            window.Close);
        try
        {
            window.Resized += size => frames.Resize(size.X, size.Y);
            window.SurfaceLost += () => frames.Paused = true;
            window.SurfaceRestored += () => frames.Paused = false;
            input.OnKeyPressed += PlayerFrames.KeyPressed;

            runtime.LoadStartupScene();
            window.Title = runtime.ProductName;
            window.SetIcon(runtime.Content, PlayerSettings.BuiltIconFileName);
            frames.Resize(window.FramebufferSize.X, window.FramebufferSize.Y);
            context.Logger.Lap("startup", "objects loaded");

            input.Initalize();
            inputSource.Initialize();
            vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);
            window.Run(frames.Frame);
            vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);
            return Application.ExitCode;
        }
        finally
        {
            frames.Dispose();
            renderer.Dispose();
            inputSource.Dispose();
            input.Dispose();
            vulkan.Device.Dispose();
            window.Dispose();
        }
    }
}
