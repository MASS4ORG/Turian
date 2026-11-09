namespace Turian.Engine.Core;

/// <summary>How far from each screen edge the display cuts into the game's rectangle, in pixels.</summary>
/// <param name="Left">Pixels covered at the left edge.</param>
/// <param name="Top">Pixels covered at the top edge.</param>
/// <param name="Right">Pixels covered at the right edge.</param>
/// <param name="Bottom">Pixels covered at the bottom edge.</param>
public readonly record struct SafeAreaInsets(int Left, int Top, int Right, int Bottom);

/// <summary>A rectangle with its origin at the top-left of the screen.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Horizontal size.</param>
/// <param name="Height">Vertical size.</param>
public readonly record struct SafeAreaRect(float X, float Y, float Width, float Height);

/// <summary>
/// The part of the screen free of notches, rounded corners and system bars. Desktop hosts report the whole window;
/// device hosts feed the platform's insets. Inject it to keep interface and gameplay elements reachable.
/// </summary>
public sealed class SafeAreaService
{
    readonly Lock gate = new();
    Int2 screen;
    SafeAreaInsets insets;

    /// <summary>Raised on the game thread when the screen size or the insets change.</summary>
    public event Action? Changed;

    /// <summary>The screen size in pixels; zero until the host reports its first frame.</summary>
    public Int2 ScreenSize { get { lock (gate) { return screen; } } }

    /// <summary>The platform's current insets, unclamped.</summary>
    public SafeAreaInsets Insets { get { lock (gate) { return insets; } } }

    /// <summary>The safe rectangle in screen pixels.</summary>
    public SafeAreaRect PixelRect
    {
        get
        {
            lock (gate)
            {
                var left = Math.Clamp(insets.Left, 0, screen.X);
                var top = Math.Clamp(insets.Top, 0, screen.Y);
                var right = Math.Clamp(insets.Right, 0, screen.X - left);
                var bottom = Math.Clamp(insets.Bottom, 0, screen.Y - top);
                return new SafeAreaRect(left, top, screen.X - left - right, screen.Y - top - bottom);
            }
        }
    }

    /// <summary>The safe rectangle in 0-to-1 screen coordinates; the whole screen before the first report.</summary>
    public SafeAreaRect NormalizedRect
    {
        get
        {
            var size = ScreenSize;
            if (size.X <= 0 || size.Y <= 0)
            {
                return new SafeAreaRect(0f, 0f, 1f, 1f);
            }

            var rect = PixelRect;
            return new SafeAreaRect(rect.X / size.X, rect.Y / size.Y, rect.Width / size.X, rect.Height / size.Y);
        }
    }

    /// <summary>Records the screen size.</summary>
    /// <param name="size">The size in pixels.</param>
    public void SetScreen(Int2 size) => Apply(size, null);

    /// <summary>Records the platform's insets.</summary>
    /// <param name="value">The insets in pixels.</param>
    public void SetInsets(SafeAreaInsets value) => Apply(null, value);

    void Apply(Int2? size, SafeAreaInsets? value)
    {
        lock (gate)
        {
            var nextScreen = size ?? screen;
            var nextInsets = value ?? insets;
            if (nextScreen == screen && nextInsets == insets)
            {
                return;
            }

            screen = nextScreen;
            insets = nextInsets;
        }

        Changed?.Invoke();
    }
}
