namespace Turian.Android;

/// <summary>Runs the normal engine session on a dedicated Android game thread.</summary>
public sealed class AndroidPlayer(AndroidSurfaceHost surface, BufferedInputSource input)
{
    /// <summary>Starts the session and runs until the activity or application requests termination.</summary>
    /// <param name="context">The content root, logger and service-container factory.</param>
    /// <returns>The application's requested exit code.</returns>
    public int Run(GamePlayerContext context)
    {
        Application.BeginSession(headless: false);
        if (!surface.WaitForSurface(Application.QuitToken))
        {
            return Application.ExitCode;
        }
        var runtime = GameRuntime.StartClient(context, input, services =>
        {
            services.AddSingleton(sp => new Vulkan(surface, context.Logger, sp.GetService<IAppSettings>()));
            services.AddSingleton<IRenderDevice>(static sp => sp.GetRequiredService<Vulkan>());
        });
        var vulkan = runtime.Services.GetRequiredService<Vulkan>();
        input.TouchEmulatesMouse = runtime.Settings.Get<InputSettings>().TouchEmulatesMouse;
        var safeArea = runtime.Services.GetRequiredService<SafeAreaService>();
        safeArea.SetInsets(surface.Insets);
        surface.InsetsChanged += safeArea.SetInsets;
        try
        {
            using var renderer = new RendererManager(surface, vulkan, runtime.Assets, context.Logger);
            renderer.Initialize();
            var presenter = PlayerFrames.CreatePresenter(UiPresenters.Find(), vulkan, input, runtime.Services);
            using var frames = new PlayerFrames(runtime, presenter, () => surface.FramebufferSize,
                texture => renderer.OverlayTexture = texture, renderer.Render, surface.Close);
            surface.Resized += Resize;
            try
            {
                runtime.LoadStartupScene();
                Resize(surface.FramebufferSize);
                surface.Run(delta =>
                {
                    frames.Paused = surface.IsPaused;
                    frames.Frame(delta);
                });
                return Application.ExitCode;
            }
            finally
            {
                surface.Resized -= Resize;
                vulkan.Vk.DeviceWaitIdle(vulkan.Device.VkDevice);
            }
            void Resize(Int2 size) => frames.Resize(size.X, size.Y);
        }
        finally
        {
            surface.InsetsChanged -= safeArea.SetInsets;
            // Session features stop first, while the services they use are still alive.
            runtime.Dispose();
            vulkan.Device.Dispose();
            (runtime.Services as IDisposable)?.Dispose();
        }
    }
}
