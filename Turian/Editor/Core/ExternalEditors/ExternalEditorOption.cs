namespace Turian.Editor.Core;

/// <summary>How an <see cref="ExternalEditorOption"/> is edited and stored.</summary>
public enum ExternalEditorOptionKind
{
    /// <summary>An on/off switch, stored as <c>true</c> or <c>false</c>.</summary>
    Toggle,

    /// <summary>Free text, such as an extra command-line flag.</summary>
    Text,

    /// <summary>One of a fixed list of values, stored as the value's text.</summary>
    Choice,
}

/// <summary>One value an <see cref="ExternalEditorOptionKind.Choice"/> option accepts.</summary>
/// <param name="Value">The text stored for it.</param>
/// <param name="Label">What the preferences show.</param>
public sealed record ExternalEditorOptionChoice(string Value, string Label);

/// <summary>
/// One setting an external editor offers beside the choice of installation, such as which window to open the project
/// in. The preferences draw it and store its value by <see cref="Key"/>.
/// </summary>
/// <param name="Key">The stable name the value is stored under.</param>
/// <param name="Label">What the preferences show.</param>
/// <param name="Kind">How the value is edited.</param>
/// <param name="DefaultValue">The value used until the user changes it.</param>
/// <param name="Description">A sentence under the label; empty for none.</param>
/// <param name="Choices">The accepted values of a <see cref="ExternalEditorOptionKind.Choice"/> option; <c>null</c> for other kinds.</param>
public sealed record ExternalEditorOption(
    string Key,
    string Label,
    ExternalEditorOptionKind Kind,
    string DefaultValue = "",
    string Description = "",
    IReadOnlyList<ExternalEditorOptionChoice>? Choices = null)
{
    /// <summary>
    /// The stored value for this option, or its default when none was stored. A choice that is no longer on offer,
    /// for example after the editor's brick changed, also gives the default.
    /// </summary>
    /// <param name="stored">The values stored for the editor; may be <c>null</c>.</param>
    /// <returns>The value as stored text.</returns>
    public string ValueIn(IReadOnlyDictionary<string, string>? stored)
    {
        var value = stored is not null && stored.TryGetValue(Key, out var found) ? found : DefaultValue;
        return Accepts(value) ? value : DefaultValue;
    }

    /// <summary>Whether <paramref name="value"/> can be stored: any text, except for a choice that must be listed.</summary>
    /// <param name="value">The text to check.</param>
    /// <returns><c>true</c> when the option takes the value.</returns>
    public bool Accepts(string value) =>
        Kind != ExternalEditorOptionKind.Choice
        || Choices?.Any(choice => string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase)) == true;
}
