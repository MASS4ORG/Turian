namespace Turian.Engine.UI;

/// <summary>
/// Feeds a <see cref="Gui"/> frame from the engine's screen-space input, bridging
/// <see cref="Guinevere.IInputHandler"/> to an explicitly supplied <see cref="IInputSource"/>.
///
/// <para>
/// Pointer coordinates are in viewport pixels with the origin at the top-left, which
/// is what Guinevere expects for a full-screen or camera-space panel. World-space
/// panels use a separate handler that remaps a camera ray onto the panel surface.
/// </para>
/// </summary>
/// <remarks>
/// <see cref="Guinevere.KeyboardKey"/> and <see cref="Guinevere.MouseButton"/> share
/// GLFW-derived numeric values with <c>Silk.NET.Input</c> for the common range, so the
/// mapping is a direct <see cref="int"/> cast — the same approach Guinevere's own
/// Silk.NET integration uses.
/// </remarks>
/// <summary>
/// An <see cref="IInputHandler"/> the <see cref="UiRuntime"/> can drive per frame: it carries the
/// canvas scale a scaled panel is rasterised at and a per-frame reset hook. Implemented by the
/// screen-space <see cref="EngineInputHandler"/> and the world-space <c>WorldPanelInputHandler</c>.
/// </summary>
public interface IUiFrameInput : IInputHandler
{
    /// <summary>Canvas scale the UI is rasterised at (pointer coords are divided by it).</summary>
    float CanvasScale { get; set; }

    /// <summary>Clears per-frame buffers (typed characters). Call after each Guinevere frame.</summary>
    void EndFrame();
}

/// <summary>
/// Screen-space input handler that translates the engine's <see cref="IInputSource"/> state into
/// a pointer/keyboard/mouse input frame for a full-screen or camera-space
/// <see cref="Gui"/>, including buffered text input.
/// </summary>
public sealed class EngineInputHandler : IUiFrameInput
{
    readonly IInputSource? source;
    string typedCharacters = string.Empty;

    /// <summary>Creates a screen-space handler. Without a source all input is neutral.</summary>
    public EngineInputHandler(IInputSource? source = null) => this.source = source;

    /// <summary>
    /// The canvas scale the UI is currently rasterized at (<see cref="UiScale"/>). Pointer
    /// coordinates come from the engine in physical viewport pixels; the layout the pointer is
    /// hit-tested against is in logical units (physical ÷ scale), so the reported position is
    /// divided by this. 1 means physical and logical coincide.
    /// </summary>
    public float CanvasScale { get; set; } = 1f;

    /// <summary>
    /// Always <c>false</c>: the engine <see cref="IInputSource"/> reports individual
    /// keys, not a whole-keyboard "any key" flag. Only <c>Dropdown</c> reads this, and
    /// it treats <c>false</c> as "no keyboard activity", which is the safe default.
    /// Revisit alongside full text-input parity.
    /// </summary>
    public bool IsAnyKeyDown => false;

    float Inv => CanvasScale is > 0f and not 1f ? 1f / CanvasScale : 1f;

    /// <inheritdoc/>
    public Vector2 MousePosition => (source?.MousePosition ?? Vector2.Zero) * Inv;

    /// <inheritdoc/>
    public Vector2 MouseDelta => (source?.MouseDelta ?? Vector2.Zero) * Inv;

    /// <inheritdoc/>
    public Vector2 PrevMousePosition => ((source?.MousePosition ?? Vector2.Zero) - (source?.MouseDelta ?? Vector2.Zero)) * Inv;

    /// <inheritdoc/>
    public float MouseWheelDelta => source?.MouseScrollDelta ?? 0f;

    /// <inheritdoc/>
    public bool IsKeyDown(KeyboardKey keyboardKey) => source?.IsKeyDown((SilkKey)(int)keyboardKey) ?? false;

    /// <inheritdoc/>
    public bool IsKeyPressed(KeyboardKey keyboardKey) => source?.WasKeyPressed((SilkKey)(int)keyboardKey) ?? false;

    /// <inheritdoc/>
    public bool IsKeyUp(KeyboardKey keyboardKey) => !IsKeyDown(keyboardKey);

    /// <inheritdoc/>
    public bool IsMouseButtonDown(MouseButton button) => source?.IsMouseButtonDown((SilkMouseButton)(int)button) ?? false;

    /// <inheritdoc/>
    public bool IsMouseButtonPressed(MouseButton button) => source?.WasMouseButtonPressed((SilkMouseButton)(int)button) ?? false;

    /// <inheritdoc/>
    public bool IsMouseButtonUp(MouseButton button) => !IsMouseButtonDown(button);

    /// <inheritdoc/>
    public string GetTypedCharacters() => typedCharacters;

    /// <summary>
    /// Text clipboard access is not yet abstracted by the engine; returns empty.
    /// Wired up with the text-input parity work (paste in <c>TextField</c>).
    /// </summary>
    public string GetClipboardText() => string.Empty;

    /// <inheritdoc cref="GetClipboardText"/>
    public void SetClipboardText(string text) { }

    /// <summary>
    /// Sets the characters typed this frame, to be reported by
    /// <see cref="GetTypedCharacters"/>. Called by the UI runtime once a per-frame
    /// character source exists; today it is only exercised by tests.
    /// </summary>
    /// <param name="text">The characters typed since the previous frame.</param>
    public void SetTypedCharacters(string text) => typedCharacters = text;

    /// <summary>Clears the per-frame typed-character buffer. Call after each Guinevere frame.</summary>
    public void EndFrame() => typedCharacters = string.Empty;
}
