namespace Gaya.Plugin.Turian;

/// <summary>The languages the studio shell ships, offered as a dropdown on its Language page.</summary>
public enum EditorLanguage : byte
{
    /// <summary>The editor's authored language and the default.</summary>
    [EnumLabel("English")]
    English = 0,

    /// <summary>Brazilian Portuguese, served from the embedded <c>pt-BR.strings</c> table.</summary>
    [EnumLabel("Português (Brasil)")]
    PortugueseBrazil = 1,
}

/// <summary>
/// The language the editor's own interface is drawn in, kept in the user scope so it follows the
/// user across projects. Defaults to <see cref="EditorLanguage.English"/>, as  <see cref="EditorLanguage"/> enum's default.
/// </summary>
[EditorSetting("General", Description = "The language the editor's own interface is drawn in.")]
public sealed class StudioLanguageSettings
{
    /// <summary>The language of the editor interface.</summary>
    [EditorSetting("Language", Description = "The language of the editor interface.")]
    public EditorLanguage Language { get; set; } = EditorLanguage.English;
}
