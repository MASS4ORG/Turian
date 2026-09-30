namespace Turian.Engine.UI;

/// <summary>
/// Input for a <see cref="UiRenderMode.WorldSpace"/> panel. The pointer position is a projection of
/// the real cursor (or another ray) onto the panel surface, set each frame by
/// <see cref="SetPointer"/>; buttons, wheel, keys and typed characters pass through from the engine
/// supplied <see cref="IInputSource"/> so a hovered widget still clicks and a focused field still types.
/// </summary>
public sealed class WorldPanelInputHandler : IUiFrameInput
{
    // Off-panel sentinel: far outside any layout rect, so nothing hovers when the ray misses.
    static readonly Vector2 OffPanel = new(-100_000f, -100_000f);

    readonly IInputSource? source;
    Vector2 position = OffPanel;
    Vector2 previous = OffPanel;
    string typedCharacters = string.Empty;

    /// <summary>Creates a panel handler. Without a source all input is neutral.</summary>
    public WorldPanelInputHandler(IInputSource? source = null) => this.source = source;

    /// <summary>Unused by world panels (they rasterise 1:1 with <c>PanelSize</c>); kept for the interface.</summary>
    public float CanvasScale { get; set; } = 1f;

    /// <summary>
    /// Sets the pointer position in panel pixels for this frame, or clears it when the pointer is
    /// not over the panel.
    /// </summary>
    /// <param name="panelPixels">Position in panel pixels, or <c>null</c> when the ray misses.</param>
    public void SetPointer(Vector2? panelPixels)
    {
        previous = position;
        position = panelPixels ?? OffPanel;
    }

    /// <inheritdoc/>
    public Vector2 MousePosition => position;

    /// <inheritdoc/>
    public Vector2 MouseDelta => position == OffPanel || previous == OffPanel ? Vector2.Zero : position - previous;

    /// <inheritdoc/>
    public Vector2 PrevMousePosition => previous;

    /// <inheritdoc/>
    public float MouseWheelDelta => source?.MouseScrollDelta ?? 0f;

    /// <inheritdoc/>
    public bool IsAnyKeyDown => false;

    /// <inheritdoc/>
    public bool IsKeyDown(KeyboardKey keyboardKey) => source?.IsKeyDown((SilkKey)(int)keyboardKey) ?? false;

    /// <inheritdoc/>
    public bool IsKeyPressed(KeyboardKey keyboardKey) => source?.WasKeyPressed((SilkKey)(int)keyboardKey) ?? false;

    /// <inheritdoc/>
    public bool IsKeyUp(KeyboardKey keyboardKey) => !IsKeyDown(keyboardKey);

    /// <inheritdoc/>
    public bool IsMouseButtonDown(MouseButton button) =>
        !IsOffPanel && (source?.IsMouseButtonDown((SilkMouseButton)(int)button) ?? false);

    /// <inheritdoc/>
    public bool IsMouseButtonPressed(MouseButton button) =>
        !IsOffPanel && (source?.WasMouseButtonPressed((SilkMouseButton)(int)button) ?? false);

    /// <inheritdoc/>
    public bool IsMouseButtonUp(MouseButton button) => !IsMouseButtonDown(button);

    /// <inheritdoc/>
    public string GetTypedCharacters() => typedCharacters;

    /// <inheritdoc/>
    public string GetClipboardText() => string.Empty;

    /// <inheritdoc/>
    public void SetClipboardText(string text) { }

    /// <summary>Sets the characters typed this frame (for a focused world-panel text field).</summary>
    /// <param name="text">The characters typed since the previous frame.</param>
    public void SetTypedCharacters(string text) => typedCharacters = text;

    /// <inheritdoc/>
    public void EndFrame() => typedCharacters = string.Empty;

    bool IsOffPanel => position == OffPanel;
}
