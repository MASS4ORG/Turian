[assembly: Turian.Engine.Core.GamePlayer(typeof(Turian.Engine.Core.VulkanPlayer))]

namespace Turian.Engine.Core;

/// <summary>
/// Plays a client build: opens the GLFW window, renders each loaded scene's primary camera with Vulkan, routes
/// window input to the game and composites the installed interface package's overlay.
/// </summary>
public sealed class VulkanPlayer : IGamePlayer
{
    /// <inheritdoc />
    public int Run(GamePlayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Application.BeginSession(headless: false);

        var window = new WindowManager { ShowStats = context.Development };
        var input = new InputManager(window, context.Logger);
        var inputSource = new SilkInputSource(input);
        var runtime = GameRuntime.Start(context, inputSource, services =>
        {
            services.AddSingleton(sp => new Vulkan(window, context.Logger, sp.GetService<IAppSettings>()));
            services.AddSingleton<IRenderDevice>(sp => sp.GetRequiredService<Vulkan>());
        });
        var vulkan = runtime.Services.GetRequiredService<Vulkan>();
        window.Initialize();
        var renderer = new RendererManager(window, vulkan, runtime.Assets, context.Logger);
        renderer.Initialize();
        var presenter = PlayerFrames.CreatePresenter(UiPresenters.Find(), vulkan, inputSource, runtime.Locale);
        var frames = new PlayerFrames(runtime, presenter,
            () => new Int2(window.Window.FramebufferSize.X, window.Window.FramebufferSize.Y),
            texture => renderer.OverlayTexture = texture,
            renderer.Render,
            window.Window.Close);
        try
        {
            window.Window.Render += frames.Frame;
            window.Window.FramebufferResize += size => frames.Resize(size.X, size.Y);
            input.OnKeyPressed += PlayerFrames.KeyPressed;

            runtime.LoadStartupScene();
            window.Title = runtime.ProductName;
            window.SetIcon(Path.Combine(runtime.ProjectDirectory, PlayerSettings.BuiltIconFileName));
            frames.Resize(window.Window.FramebufferSize.X, window.Window.FramebufferSize.Y);
            context.Logger.Lap("startup", "objects loaded");

            input.Initalize();
            inputSource.Initialize();
            vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);
            window.Run();
            vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);
            return Application.ExitCode;
        }
        finally
        {
            frames.Dispose();
            window.Dispose();
            renderer.Dispose();
            inputSource.Dispose();
            input.Dispose();
            vulkan.Device.Dispose();
        }
    }
}
