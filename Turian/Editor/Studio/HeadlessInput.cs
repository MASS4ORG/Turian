namespace Turian.Editor.Studio;

/// <summary>A no-op <see cref="IInputHandler"/> for the headless <c>--dump</c> render path.</summary>
sealed class HeadlessInput : IInputHandler
{
    public bool IsAnyKeyDown => false;
    public Vector2 MousePosition => new(-1f, -1f);
    public Vector2 MouseDelta => Vector2.Zero;
    public Vector2 PrevMousePosition => new(-1f, -1f);
    public float MouseWheelDelta => 0f;

    public bool IsKeyDown(KeyboardKey keyboardKey) => false;
    public bool IsKeyPressed(KeyboardKey keyboardKey) => false;
    public bool IsKeyUp(KeyboardKey keyboardKey) => true;
    public bool IsMouseButtonDown(MouseButton button) => false;
    public bool IsMouseButtonPressed(MouseButton button) => false;
    public bool IsMouseButtonUp(MouseButton button) => true;

    public string GetTypedCharacters() => string.Empty;
    public string GetClipboardText() => string.Empty;
    public void SetClipboardText(string text) { }
}
