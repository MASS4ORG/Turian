namespace Gaya.Sdk;

/// <summary>Modifier keys a shortcut may require. The left and right key of a pair are equivalent.</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifier is held.</summary>
    None = 0,

    /// <summary>Either Control key.</summary>
    Ctrl = 1,

    /// <summary>Either Shift key.</summary>
    Shift = 2,

    /// <summary>Either Alt key.</summary>
    Alt = 4,
}

/// <summary>One key press together with the modifiers held with it, such as <c>Ctrl+Shift+S</c>.</summary>
/// <param name="Key">The key that completes the stroke.</param>
/// <param name="Modifiers">The modifiers that must be held, matched exactly.</param>
public readonly record struct KeyStroke(KeyboardKey Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    /// <summary>The empty stroke, which never matches and reads as "unbound".</summary>
    public static KeyStroke None => new(KeyboardKey.Unknown);

    /// <summary>Whether this is the empty stroke.</summary>
    public bool IsNone => Key == KeyboardKey.Unknown;

    /// <summary>The stroke as a menu shows it, e.g. <c>Ctrl+Shift+S</c>.</summary>
    /// <returns>The display form, or an empty string for <see cref="None"/>.</returns>
    public override string ToString() =>
        IsNone
            ? string.Empty
            : string.Concat(
                Modifiers.HasFlag(KeyModifiers.Ctrl) ? "Ctrl+" : "",
                Modifiers.HasFlag(KeyModifiers.Alt) ? "Alt+" : "",
                Modifiers.HasFlag(KeyModifiers.Shift) ? "Shift+" : "",
                KeyName(Key));

    /// <summary>Reads a stroke written the way <see cref="ToString"/> prints one.</summary>
    /// <param name="text">The text to read, e.g. <c>Ctrl+Alt+F2</c>.</param>
    /// <param name="stroke">The parsed stroke, or <see cref="None"/> when the text is not a stroke.</param>
    /// <returns>True when the whole text was understood.</returns>
    public static bool TryParse(string? text, out KeyStroke stroke)
    {
        stroke = None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = KeyModifiers.None;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyModifiers.Ctrl,
                "SHIFT" => KeyModifiers.Shift,
                "ALT" => KeyModifiers.Alt,
                _ => KeyModifiers.None,
            };

            if (modifier != KeyModifiers.None && i < parts.Length - 1)
            {
                modifiers |= modifier;
                continue;
            }

            if (!TryParseKey(part, out var key)) return false;
            stroke = new KeyStroke(key, modifiers);
            return i == parts.Length - 1;
        }

        return false;
    }

    /// <summary>
    /// The stroke completed this frame, or null when no key that can end one went down. Modifier keys
    /// only ever qualify a stroke, so holding Shift alone reads as nothing.
    /// </summary>
    /// <param name="input">This frame's input.</param>
    public static KeyStroke? Read(IInputHandler input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var modifiers = KeyModifiers.None;
        if (Held(input, KeyboardKey.LeftControl, KeyboardKey.RightControl)) modifiers |= KeyModifiers.Ctrl;
        if (Held(input, KeyboardKey.LeftShift, KeyboardKey.RightShift)) modifiers |= KeyModifiers.Shift;
        if (Held(input, KeyboardKey.LeftAlt, KeyboardKey.RightAlt)) modifiers |= KeyModifiers.Alt;

        foreach (var key in Bindable)
            if (input.IsKeyPressed(key))
                return new KeyStroke(key, modifiers);

        return null;
    }

    /// <summary>Every key a stroke may end on — everything except the modifiers themselves.</summary>
    public static IReadOnlyList<KeyboardKey> Bindable { get; } =
        [.. Enum.GetValues<KeyboardKey>().Where(key => key != KeyboardKey.Unknown && !IsModifier(key)).Distinct()];

    /// <summary>Whether a key only ever qualifies another, so it can never complete a stroke.</summary>
    /// <param name="key">The key to test.</param>
    /// <returns>True for Control, Shift, Alt and Super.</returns>
    public static bool IsModifier(KeyboardKey key) => key
        is KeyboardKey.LeftControl or KeyboardKey.RightControl
        or KeyboardKey.LeftShift or KeyboardKey.RightShift
        or KeyboardKey.LeftAlt or KeyboardKey.RightAlt
        or KeyboardKey.LeftSuper or KeyboardKey.RightSuper;

    static bool Held(IInputHandler input, KeyboardKey left, KeyboardKey right) =>
        input.IsKeyDown(left) || input.IsKeyDown(right);

    /// <summary>Digits and punctuation print as themselves; everything else keeps its enum name.</summary>
    static string KeyName(KeyboardKey key) => key switch
    {
        >= KeyboardKey.D0 and <= KeyboardKey.D9 => ((char)('0' + (key - KeyboardKey.D0))).ToString(),
        KeyboardKey.Comma => ",",
        KeyboardKey.Period => ".",
        KeyboardKey.Minus => "-",
        KeyboardKey.Equal => "=",
        KeyboardKey.Slash => "/",
        KeyboardKey.Backslash => "\\",
        KeyboardKey.Semicolon => ";",
        KeyboardKey.Apostrophe => "'",
        KeyboardKey.GraveAccent => "`",
        KeyboardKey.LeftBracket => "[",
        KeyboardKey.RightBracket => "]",
        _ => key.ToString(),
    };

    static bool TryParseKey(string text, out KeyboardKey key)
    {
        key = text switch
        {
            "," => KeyboardKey.Comma,
            "." => KeyboardKey.Period,
            "-" => KeyboardKey.Minus,
            "=" => KeyboardKey.Equal,
            "/" => KeyboardKey.Slash,
            "\\" => KeyboardKey.Backslash,
            ";" => KeyboardKey.Semicolon,
            "'" => KeyboardKey.Apostrophe,
            "`" => KeyboardKey.GraveAccent,
            "[" => KeyboardKey.LeftBracket,
            "]" => KeyboardKey.RightBracket,
            _ => KeyboardKey.Unknown,
        };

        if (key != KeyboardKey.Unknown) return true;
        if (text.Length == 1 && char.IsAsciiDigit(text[0])) key = KeyboardKey.D0 + (text[0] - '0');
        else if (!Enum.TryParse(text, ignoreCase: true, out key)) key = KeyboardKey.Unknown;

        return key != KeyboardKey.Unknown;
    }
}

/// <summary>
/// The well-known shortcut contexts. A context is a panel id, so a binding fires only while that
/// panel has focus; <see cref="Global"/> fires wherever focus is.
/// </summary>
public static class ShortcutContexts
{
    /// <summary>The context of a binding that works anywhere in the studio.</summary>
    public const string Global = "";

    /// <summary>Whether a context is the global one.</summary>
    /// <param name="context">The context to test.</param>
    /// <returns>True for <see cref="Global"/> and for a missing context.</returns>
    public static bool IsGlobal(string? context) => string.IsNullOrEmpty(context);
}

/// <summary>
/// A key sequence bound to a command inside a context. One stroke is the common case; a second one
/// makes it a chord, where the first stroke arms the sequence and the second completes it.
/// </summary>
/// <param name="CommandId">The command the sequence invokes.</param>
/// <param name="Stroke">The stroke that fires the command, or arms the chord.</param>
/// <param name="Second">The stroke that completes a chord, or null for a single-stroke binding.</param>
/// <param name="Context">
/// The panel id the binding belongs to, or <see cref="ShortcutContexts.Global"/> for one that works
/// anywhere. A panel-scoped binding wins over a global one on the same sequence.
/// </param>
public sealed record KeyBinding(
    string CommandId,
    KeyStroke Stroke,
    KeyStroke? Second = null,
    string Context = ShortcutContexts.Global)
{
    /// <summary>Builds a single-stroke binding from a key and its modifiers.</summary>
    /// <param name="commandId">The command the stroke invokes.</param>
    /// <param name="key">The key that fires it.</param>
    /// <param name="modifiers">The modifiers that must be held.</param>
    /// <param name="context">The panel id the binding belongs to.</param>
    public KeyBinding(string commandId, KeyboardKey key, KeyModifiers modifiers = KeyModifiers.None,
        string context = ShortcutContexts.Global)
        : this(commandId, new KeyStroke(key, modifiers), null, context)
    {
    }

    /// <summary>Whether the sequence needs two strokes.</summary>
    public bool IsChord => Second is not null;

    /// <summary>The sequence as a menu shows it, e.g. <c>Ctrl+K, Ctrl+S</c>.</summary>
    public string Display => Second is { } second ? $"{Stroke}, {second}" : Stroke.ToString();

    /// <summary>Reads the sequence <see cref="Display"/> prints, with the strokes separated by a comma.</summary>
    /// <param name="text">The sequence to read.</param>
    /// <param name="strokes">The parsed strokes, empty when the text is not a sequence.</param>
    /// <returns>True when every stroke was understood and there were at most two.</returns>
    public static bool TryParseSequence(string? text, out (KeyStroke First, KeyStroke? Second) strokes)
    {
        strokes = (KeyStroke.None, null);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 2) return false;
        if (!KeyStroke.TryParse(parts[0], out var first)) return false;
        if (parts.Length == 1)
        {
            strokes = (first, null);
            return true;
        }

        if (!KeyStroke.TryParse(parts[1], out var second)) return false;
        strokes = (first, second);
        return true;
    }
}

/// <summary>Collects <see cref="KeyBinding"/> contributions.</summary>
public interface IShortcutRegistry
{
    /// <summary>Adds a default binding. Re-adding the same command and context replaces the earlier one.</summary>
    /// <param name="binding">The binding to add.</param>
    void Add(KeyBinding binding);

    /// <summary>Drops every binding for a command, for a contributor whose set changes at runtime.</summary>
    /// <param name="commandId">The command whose bindings are removed.</param>
    void Remove(string commandId);
}

/// <summary>One command's shortcut as the keybindings editor sees it: what was declared and what applies.</summary>
/// <param name="CommandId">The command the shortcut invokes.</param>
/// <param name="Default">The binding the contributing plugin declared.</param>
/// <param name="Effective">The binding in force, or null when the user cleared it.</param>
public sealed record ShortcutEntry(string CommandId, KeyBinding Default, KeyBinding? Effective)
{
    /// <summary>The context the shortcut belongs to; the user rebinds the keys, never the context.</summary>
    public string Context => Default.Context;

    /// <summary>Whether the user changed this shortcut away from what was declared.</summary>
    public bool IsModified => Effective?.Display != Default.Display;

    /// <summary>The sequence in force as a menu shows it, or an empty string when unbound.</summary>
    public string Display => Effective?.Display ?? string.Empty;
}

/// <summary>Two or more commands sharing one key sequence in contexts that overlap.</summary>
/// <param name="Display">The sequence they share.</param>
/// <param name="Context">The context the clash happens in.</param>
/// <param name="CommandIds">The commands bound to it.</param>
public sealed record ShortcutConflict(string Display, string Context, IReadOnlyList<string> CommandIds);

/// <summary>
/// The studio's shortcuts: every contributed binding, the user's overrides, and the dispatch that
/// turns a key press in a focused panel into a command id. Resolved from DI by the keybindings
/// editor and by whatever needs to show a command's chord.
/// </summary>
public interface IShortcutService : IShortcutRegistry
{
    /// <summary>Every contributed shortcut with the binding currently in force, ordered by command id.</summary>
    IReadOnlyList<ShortcutEntry> Entries { get; }

    /// <summary>Raised when a binding is added, removed, overridden or reset.</summary>
    event Action? Changed;

    /// <summary>The sequence a menu shows beside a command, or an empty string when it has none.</summary>
    /// <param name="commandId">The command to look up.</param>
    /// <returns>The display form of the binding in force.</returns>
    string DisplayFor(string commandId);

    /// <summary>
    /// Rebinds a command. Passing <see cref="KeyStroke.None"/> as the first stroke clears the
    /// shortcut, which is how a user removes one without giving it another sequence.
    /// </summary>
    /// <param name="commandId">The command to rebind.</param>
    /// <param name="stroke">The stroke that fires it.</param>
    /// <param name="second">The stroke that completes a chord, or null.</param>
    void Rebind(string commandId, KeyStroke stroke, KeyStroke? second = null);

    /// <summary>Restores a command's declared binding.</summary>
    /// <param name="commandId">The command to reset.</param>
    void Reset(string commandId);

    /// <summary>Restores every declared binding, dropping all overrides.</summary>
    void ResetAll();

    /// <summary>Sequences bound to more than one command in overlapping contexts.</summary>
    IReadOnlyList<ShortcutConflict> Conflicts { get; }

    /// <summary>
    /// Whether a rebinding editor is listening for the next key. While it is, the studio dispatches
    /// nothing, so the sequence being captured cannot also run the command it is currently bound to.
    /// </summary>
    bool IsCapturing { get; set; }

    /// <summary>The commands a sequence would clash with, ignoring the one being rebound.</summary>
    /// <param name="commandId">The command being rebound, which is never its own conflict.</param>
    /// <param name="stroke">The proposed first stroke.</param>
    /// <param name="second">The proposed second stroke, or null.</param>
    /// <returns>The command ids already using the sequence in an overlapping context.</returns>
    IReadOnlyList<string> ConflictsFor(string commandId, KeyStroke stroke, KeyStroke? second = null);
}

/// <summary>
/// Which panel the studio considers focused, and therefore which panel-scoped shortcuts fire. The
/// workbench sets it as the user clicks into a panel or activates its tab.
/// </summary>
public interface IFocusTracker
{
    /// <summary>The focused panel's id, or an empty string when focus is not in a panel.</summary>
    string ActivePanelId { get; }

    /// <summary>Marks a panel focused.</summary>
    /// <param name="panelId">The panel that took focus.</param>
    void Focus(string panelId);

    /// <summary>Raised with the new panel id whenever focus moves.</summary>
    event Action<string>? Changed;
}

/// <summary>
/// Reaches a live panel instance by id, so a focus-scoped command can act on the panel it belongs to
/// without that panel having to register the command itself.
/// </summary>
public interface IPanelAccessor
{
    /// <summary>The panel's instance, created on first use, or null when no such panel is registered.</summary>
    /// <param name="panelId">The panel to reach.</param>
    IPanel? Panel(string panelId);

    /// <summary>The panel's title, or the id itself when no such panel is registered.</summary>
    /// <param name="panelId">The panel to name.</param>
    string Title(string panelId);
}

/// <summary>The commands currently registered, for the palette and the keybindings editor.</summary>
public interface ICommandCatalog
{
    /// <summary>Every registered command, ordered by title.</summary>
    IReadOnlyList<CommandDescriptor> Commands { get; }

    /// <summary>Looks a command up by id.</summary>
    /// <param name="commandId">The command to find.</param>
    /// <returns>The descriptor, or null when nothing is registered under that id.</returns>
    CommandDescriptor? Find(string commandId);
}

/// <summary>Ids of the commands the workbench itself contributes, before any plugin is configured.</summary>
public static class ShellCommands
{
    /// <summary>Opens or closes the command palette.</summary>
    public const string CommandPalette = "gaya.shell.commandPalette";
}
