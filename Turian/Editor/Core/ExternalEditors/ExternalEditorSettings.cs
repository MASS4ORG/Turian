namespace Turian.Editor.Core;

/// <summary>
/// The preference naming the editor code opens in. It lives per project and per user by default, so the choice is never
/// committed, and may also be stored for the user, the project or the whole studio.
/// </summary>
[Gaya.EditorSetting("External Tools/External Editor", Id = PageId, Scope = Gaya.Sdk.SettingsScope.Personal,
    Scopes = Gaya.Sdk.SettingsScope.Studio | Gaya.Sdk.SettingsScope.User | Gaya.Sdk.SettingsScope.Workspace,
    Description = "The editor Studio opens scripts and the project in.")]
public sealed class ExternalEditorSettings
{
    /// <summary>The storage id of the page, shared by Studio and the command line.</summary>
    public const string PageId = "gaya.turian.externalEditor";

    /// <summary>The name of the one stored member, which holds the whole choice.</summary>
    public const string Member = nameof(Editor);

    /// <summary>The chosen editor; the default means the operating system's default program.</summary>
    [Gaya.EditorSetting("External Editor", Description = "Choose an installed editor, browse for one, or describe your own.")]
    public ExternalEditorChoice Editor { get; set; } = new();
}
