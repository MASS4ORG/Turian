using Silk.NET.Maths;

namespace Turian.Engine.Core;

/// <summary>
/// Manages the application window and provides an interface for window-related operations.
/// </summary>
public class WindowManager : IDisposable
{
    const int widthInitial = 1800;
    const int heightInitial = 1200;
    const long fpsUpdateInterval = 5 * 10_000;

    /// <summary>
    /// Gets the window associated with this manager.
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

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowManager"/> class.
    /// </summary>
    public WindowManager()
    {
        var options = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(widthInitial, heightInitial),
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

    /// <summary>
    /// Initializes the window manager and sets up frame rate tracking.
    /// </summary>
    public void Initialize()
    {
        fpsLastUpdate = DateTime.Now.Ticks;
        Window.Update += Update;
    }

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
            w.Title = $"{title} | {Window.Size.X}x{Window.Size.Y} | {fps,-8: 0} fps";
        }
        fpsLastUpdate = DateTime.Now.Ticks;
        frameCount = 0;
    }

    /// <summary>
    /// Runs the application's main loop.
    /// </summary>
    public void Run()
    {
        Window.Run();
    }

    /// <summary>
    /// Disposes of the window manager and releases associated resources.
    /// </summary>
    public void Dispose()
    {
        Window.Update -= Update;
        Window.Dispose();

        GC.SuppressFinalize(this);
    }
}
