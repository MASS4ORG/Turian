namespace Gaya.Plugin.Turian;

/// <summary>
/// Translates Guinevere input into the Silk.NET key and button enums the engine exposes to gameplay
/// code, so a script reads the same <see cref="Silk.NET.Input.Key"/> values whether it runs in the
/// Studio's play mode or in the standalone runtime. The Guinevere counterpart of StudioA's
/// <c>AvaloniaInputMap</c>.
/// </summary>
static class GuinevereInputMap
{
    /// <summary>Every key the Game panel watches, so a frame can diff held state into press edges.</summary>
    public static IReadOnlyList<KeyboardKey> Keys { get; } =
        [.. Enum.GetValues<KeyboardKey>().Where(key => key != KeyboardKey.Unknown)];

    /// <summary>Every mouse button the Game panel watches.</summary>
    public static IReadOnlyList<MouseButton> Buttons { get; } = [.. Enum.GetValues<MouseButton>()];

    /// <summary>
    /// Maps a Guinevere key, or returns <c>null</c> when the engine has no equivalent. Both enums are
    /// GLFW usage codes — <c>Space</c> is 32 and <c>Escape</c> is 256 in each — so the cast is the
    /// mapping; <see cref="Enum.IsDefined{TEnum}"/> drops the few Guinevere names Silk does not carry.
    /// </summary>
    /// <param name="key">The Guinevere key.</param>
    /// <returns>The engine's key, or null.</returns>
    public static SilkKey? ToSilkKey(KeyboardKey key) =>
        Enum.IsDefined((SilkKey)(int)key) ? (SilkKey)(int)key : null;

    /// <summary>Maps a Guinevere mouse button, which shares the same ordering.</summary>
    /// <param name="button">The Guinevere button.</param>
    /// <returns>The engine's button, or null.</returns>
    public static SilkMouseButton? ToSilkMouseButton(MouseButton button) =>
        Enum.IsDefined((SilkMouseButton)(int)button) ? (SilkMouseButton)(int)button : null;
}
