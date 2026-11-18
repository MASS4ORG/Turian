namespace Turian.Editor.Core;

/// <summary>How an <see cref="ExternalEditorOption"/> is edited and stored.</summary>
public enum ExternalEditorOptionKind
{
    /// <summary>An on/off switch, stored as <c>true</c> or <c>false</c>.</summary>
    Toggle,

    /// <summary>Free text, such as an extra command-line flag.</summary>
    Text,
}

/// <summary>
/// One setting an external editor offers beside the choice of installation, such as reusing an existing window. The
/// preferences draw it and store its value by <see cref="Key"/>.
/// </summary>
/// <param name="Key">The stable name the value is stored under.</param>
/// <param name="Label">What the preferences show.</param>
/// <param name="Kind">How the value is edited.</param>
/// <param name="DefaultValue">The value used until the user changes it.</param>
/// <param name="Description">A sentence under the label; empty for none.</param>
public sealed record ExternalEditorOption(
    string Key,
    string Label,
    ExternalEditorOptionKind Kind,
    string DefaultValue = "",
    string Description = "")
{
    /// <summary>The stored value for this option, or its default when none was stored.</summary>
    /// <param name="stored">The values stored for the editor; may be <c>null</c>.</param>
    /// <returns>The value as stored text.</returns>
    public string ValueIn(IReadOnlyDictionary<string, string>? stored) =>
        stored is not null && stored.TryGetValue(Key, out var value) ? value : DefaultValue;
}
