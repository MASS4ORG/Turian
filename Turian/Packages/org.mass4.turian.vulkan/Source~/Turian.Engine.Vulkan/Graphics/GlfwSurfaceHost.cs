namespace Turian.Engine.Core;

/// <summary>
/// The desktop <see cref="ISurfaceHost"/>: a Silk.NET GLFW window, which also shows the game's title, icon and
/// optional frame statistics.
/// </summary>
public class GlfwSurfaceHost : ISurfaceHost
{
    const int widthInitial = 1800;
    const int heightInitial = 1200;
    const long fpsUpdateInterval = 5 * 10_000;

    /// <summary>
    /// Gets the window associated with this host.
    /// </summary>
    public IView Window { get; private set; }

    long fpsLastUpdate;
    int frameCount;
    string title = "Turian";

    /// <summary>Gets or sets the window's title, which a game sets to its product name.</summary>
    public string Title
    {
        get => title;
        set
        {
            title = value;
            if (Window is IWindow window) window.Title = value;
        }
    }

    /// <summary>Gets or sets whether the title also shows the window size and frame rate.</summary>
    public bool ShowStats { get; set; }

    /// <summary>Gets or sets the current view's submesh counts, displayed alongside the frame rate.</summary>
    public RenderCullingStats CullingStats { get; set; }

    /// <inheritdoc />
    public event Action<Int2>? Resized;

    /// <inheritdoc />
    public event Action? SurfaceLost;

    /// <inheritdoc />
    public event Action? SurfaceRestored;

    /// <inheritdoc />
    public event Action? Closing;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlfwSurfaceHost"/> class.
    /// </summary>
    public GlfwSurfaceHost()
    {
        var options = WindowOptions.DefaultVulkan with
        {
            Size = new Silk.NET.Maths.Vector2D<int>(widthInitial, heightInitial),
            Title = title
        };

        Window = Silk.NET.Windowing.Window.Create(options);
        Window.Initialize();

        if (Window.VkSurface is null)
        {
            throw new VulkanException("Windowing platform doesn't support Vulkan.");
        }
        Log.Logger.Lap("startup", "got window");
    }

    /// <inheritdoc />
    public unsafe IReadOnlyList<string> RequiredInstanceExtensions
    {
        get
        {
            var names = Window.VkSurface!.GetRequiredExtensions(out var count);
            return SilkMarshal.PtrToStringArray((nint)names, (int)count);
        }
    }

    /// <inheritdoc />
    public Int2 FramebufferSize => new(Window.FramebufferSize.X, Window.FramebufferSize.Y);

    /// <summary>
    /// Initializes the host's event forwarding and frame rate tracking.
    /// </summary>
    public void Initialize()
    {
        fpsLastUpdate = DateTime.Now.Ticks;
        Window.Update += Update;
        Window.FramebufferResize += OnFramebufferResize;
        Window.Closing += OnClosing;
        if (Window is IWindow window) window.StateChanged += OnStateChanged;
    }

    /// <summary>Gets whether the window is minimized, which the host reports as a lost surface.</summary>
    public bool IsSurfaceLost { get; private set; }

    /// <summary>
    /// Reports minimizing as a lost surface and leaving the minimized state as a restored one. While minimized the
    /// window is event driven, so the frame pump blocks on platform events instead of spinning.
    /// </summary>
    /// <param name="state">The window's new state.</param>
    internal void OnStateChanged(WindowState state)
    {
        var minimized = state == WindowState.Minimized;
        if (minimized == IsSurfaceLost) return;

        IsSurfaceLost = minimized;
        Window.IsEventDriven = minimized;
        if (minimized) SurfaceLost?.Invoke();
        else SurfaceRestored?.Invoke();
    }

    /// <inheritdoc />
    public unsafe SurfaceKHR CreateSurface(Vk vk, Instance instance) =>
        Window.VkSurface!.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();

    /// <inheritdoc />
    public unsafe void DestroySurface(Vk vk, Instance instance, SurfaceKHR surface)
    {
        ArgumentNullException.ThrowIfNull(vk);
        if (!vk.TryGetInstanceExtension(instance, out KhrSurface khrSurface)) return;

        khrSurface.DestroySurface(instance, surface, null);
    }

    /// <inheritdoc />
    public IInputContext CreateInput() => Window.CreateInput();

    /// <inheritdoc />
    public void Run(Action<double> frame)
    {
        Window.Render += frame;
        try
        {
            Window.Run();
        }
        finally
        {
            Window.Render -= frame;
        }
    }

    /// <inheritdoc />
    public void Close() => Window.Close();

    void OnFramebufferResize(Silk.NET.Maths.Vector2D<int> size) => Resized?.Invoke(new Int2(size.X, size.Y));

    void OnClosing() => Closing?.Invoke();

    void Update(double frametime)
    {
        if (!ShowStats) return;

        frameCount++;
        if (DateTime.Now.Ticks - fpsLastUpdate < fpsUpdateInterval)
        {
            return;
        }

        var fps = frameCount / (frametime * frameCount);
        if (Window is IWindow w)
        {
            w.Title = $"{title} | {Window.Size.X}x{Window.Size.Y} | {fps,-8: 0} fps"
                + $" | {CullingStats.Submitted} drawn, {CullingStats.Culled}/{CullingStats.Total} culled";
        }
        fpsLastUpdate = DateTime.Now.Ticks;
        frameCount = 0;
    }

    /// <summary>Gives the window the PNG icon at <paramref name="path"/> in the game's content, when it exists.</summary>
    /// <param name="content">The game's content root.</param>
    /// <param name="path">The icon a game's build wrote, relative to <paramref name="content"/>.</param>
    public void SetIcon(IContentDirectory content, string path)
    {
        if (TryLoadIcon(Window, content, path, out var window, out var icon)) window.SetWindowIcon(ref icon);
    }

    /// <summary>Reads the icon at <paramref name="path"/> when <paramref name="view"/> is a window and it exists.</summary>
    internal static bool TryLoadIcon(IView view, IContentDirectory content, string path,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWindow? window, out RawImage icon)
    {
        window = view as IWindow;
        var loaded = window is null ? null : LoadIcon(content, path);
        icon = loaded ?? default;
        return loaded is not null;
    }

    /// <summary>Decodes the PNG at <paramref name="path"/> into a window icon.</summary>
    /// <param name="content">The game's content root.</param>
    /// <param name="path">The icon, relative to <paramref name="content"/>.</param>
    /// <returns>The icon, or <c>null</c> when there is none.</returns>
    internal static RawImage? LoadIcon(IContentDirectory content, string path)
    {
        if (!content.Exists(path)) return null;

        var image = ImageResult.FromMemory(content.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
        return new RawImage(image.Width, image.Height, image.Data);
    }

    /// <summary>
    /// Disposes of the window and releases associated resources.
    /// </summary>
    public void Dispose()
    {
        Window.Update -= Update;
        Window.FramebufferResize -= OnFramebufferResize;
        Window.Closing -= OnClosing;
        if (Window is IWindow window) window.StateChanged -= OnStateChanged;
        Window.Dispose();

        GC.SuppressFinalize(this);
    }
}
