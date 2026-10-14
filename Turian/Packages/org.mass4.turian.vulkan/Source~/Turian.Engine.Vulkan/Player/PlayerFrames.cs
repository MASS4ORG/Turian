namespace Turian.Engine.Core;

/// <summary>
/// The frame loop of a windowed player, apart from the window it runs in: ticks the game, composites the interface
/// overlay and renders each loaded scene's primary camera, through the callbacks the window and renderer supply.
/// </summary>
/// <param name="runtime">The running game.</param>
/// <param name="presenter">The interface package's presenter, or <c>null</c> without one.</param>
/// <param name="framebufferSize">Reads the window's framebuffer size in pixels.</param>
/// <param name="setOverlay">Hands the renderer the texture composited over this frame.</param>
/// <param name="renderScene">Renders one scene root through one camera.</param>
/// <param name="close">Closes the window, ending the player's loop.</param>
public sealed class PlayerFrames(
    GameRuntime runtime,
    IUiPresenter? presenter,
    Func<Int2> framebufferSize,
    Action<Texture?> setOverlay,
    Action<double, ICamera, Node> renderScene,
    Action close) : IDisposable
{
    /// <summary>Creates the interface presenter of an installed interface package, marked as playing.</summary>
    /// <param name="factory">The interface package's factory, or <c>null</c> when none is installed.</param>
    /// <param name="vulkan">The device interface textures are created on.</param>
    /// <param name="input">Input for interface controls.</param>
    /// <param name="locale">Resolves localized text.</param>
    /// <returns>The presenter, or <c>null</c> without an interface package.</returns>
    public static IUiPresenter? CreatePresenter(IUiPresenterFactory? factory, Vulkan vulkan, IInputSource input,
        LocaleService locale)
    {
        if (factory?.Create(vulkan, input, locale) is not { } created) return null;

        created.IsPlaying = true;
        return created;
    }

    bool paused;
    bool resumed;

    /// <summary>
    /// Gets or sets whether the game is paused because its surface is lost: frames neither tick nor render, and the
    /// first frame after resuming ticks with no elapsed time, so the paused interval never reaches the game.
    /// </summary>
    public bool Paused
    {
        get => paused;
        set
        {
            if (paused && !value) resumed = true;
            paused = value;
        }
    }

    /// <summary>
    /// Runs one frame: closes the window once the game quits, otherwise ticks it, then renders the overlay and
    /// every loaded root with its primary camera. Does nothing else while <see cref="Paused"/>.
    /// </summary>
    /// <param name="deltaTime">Seconds since the previous frame.</param>
    public void Frame(double deltaTime)
    {
        if (Application.QuitToken.IsCancellationRequested)
        {
            close();
            return;
        }

        if (Paused) return;
        if (resumed)
        {
            resumed = false;
            deltaTime = 0;
        }

        runtime.Ticker.Tick(deltaTime);
        RenderOverlay(deltaTime);

        // One camera per root: they all draw to the same swapchain image, so rendering every camera in the scene
        // just overdraws — which is what a scene with several enabled cameras looks like as flicker.
        foreach (var root in Roots())
        {
            if (CameraComponent.FindPrimary(root) is { } camera) renderScene(deltaTime, camera, root);
        }
    }

    /// <summary>Gives the active scene's cameras the new framebuffer size.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    public void Resize(int width, int height)
    {
        if (runtime.Scenes.ActiveScene?.RootNode is not { } root) return;

        foreach (var camera in Node.GetComponentsInChildren<CameraComponent>(root))
            camera.Resize((uint)width, (uint)height);
    }

    /// <summary>Quits the game on Escape.</summary>
    /// <param name="key">The key pressed.</param>
    public static void KeyPressed(Key key)
    {
        if (key == Key.Escape) Application.Quit();
    }

    /// <summary>Releases the interface presenter.</summary>
    public void Dispose()
    {
        runtime.Ticker.Async.Dispose();
        presenter?.Dispose();
    }

    void RenderOverlay(double deltaTime)
    {
        var size = framebufferSize();
        if (presenter is null || size.X <= 0 || size.Y <= 0) return;

        var scenes = runtime.Scenes;
        var root = runtime.StartupRoot ?? scenes.ActiveScene?.RootNode ?? scenes.PersistentRoot;
        setOverlay(presenter.TryRenderOverlay(root, size.X, size.Y, (float)deltaTime));
    }

    IEnumerable<Node> Roots() =>
        runtime.Scenes.LoadedScenes.Select(static s => s.RootNode).Prepend(runtime.Scenes.PersistentRoot);
}
