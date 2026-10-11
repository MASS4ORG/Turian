namespace Turian.Engine.Core;

/// <summary>
/// Manages input devices and provides event handling for keyboard and mouse input.
/// </summary>
public class InputManager : IDisposable
{
    readonly List<Key> keysDown = [];

    /// <summary>
    /// Event that is raised when a key is pressed.
    /// </summary>
    public Action<Key> OnKeyPressed { get; set; } = null!;

    /// <summary>
    /// Gets the input context associated with this input manager.
    /// </summary>
    public IInputContext Input { get; }

    /// <summary>
    /// Initializes a new instance of the InputManager class.
    /// </summary>
    /// <param name="windowManager">The window manager associated with the application.</param>
    /// <param name="logger">The logger used for logging.</param>
    public InputManager(WindowManager windowManager, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(windowManager);
        Input = windowManager.Window.CreateInput();
        Input.ConnectionChanged += Connect;
        logger.Lap("run", "got camera and controls");
    }

    /// <summary>
    /// Initializes the InputManager by connecting to input devices.
    /// </summary>
    public void Initalize()
    {
        foreach (var keyboard in Input.Keyboards)
        {
            if (!keyboard.IsConnected)
            {
                continue;
            }
            Connect(keyboard, keyboard.IsConnected);
        }
        foreach (var device in Input.Mice)
        {
            device.Click += MouseOnClick;
        }
    }

    /// <summary>
    /// Connects to an input device based on its connection status.
    /// </summary>
    /// <param name="device">The input device to connect or disconnect.</param>
    /// <param name="isConnected">True if the device is connected; otherwise, false.</param>
    public void Connect(IInputDevice device, bool isConnected)
    {
        if (device is IKeyboard keyboard)
        {
            if (isConnected)
            {
                keyboard.KeyDown += KeyboardOnKeyDown;
                keyboard.KeyUp += KeyboardOnKeyUp;
                keyboard.KeyChar += KeyboardOnKeyChar;
            }
            else
            {
                keyboard.KeyDown -= KeyboardOnKeyDown;
                keyboard.KeyUp -= KeyboardOnKeyUp;
                keyboard.KeyChar -= KeyboardOnKeyChar;
            }
        }
    }

    /// <summary>
    /// Disposes of the input manager and releases associated resources.
    /// </summary>
    public void Dispose()
    {
        Input.Dispose();

        GC.SuppressFinalize(this);
    }

    void MouseOnClick(IMouse mouse, MouseButton button, Vector2 vector) { }

    void KeyboardOnKeyChar(IKeyboard arg1, char arg2) { }

    void KeyboardOnKeyUp(IKeyboard arg1, Key arg2, int _)
    {
        keysDown.RemoveAt(keysDown.IndexOf(arg2));
    }

    void KeyboardOnKeyDown(IKeyboard arg1, Key arg2, int _)
    {
        keysDown.Add(arg2);
        OnKeyPressed.Invoke(arg2);
    }
}
