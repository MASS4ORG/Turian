namespace Gaya.Host;

/// <summary>The custom settings editors plugins registered, shared by the Settings panel.</summary>
public sealed class SettingsFieldEditors : ISettingsFieldEditors
{
    readonly Dictionary<(string Page, string Member), ISettingsFieldEditor> editors = [];

    /// <inheritdoc />
    public void Register(string pageId, string member, ISettingsFieldEditor editor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        ArgumentNullException.ThrowIfNull(editor);

        editors[(pageId, member.ToUpperInvariant())] = editor;
    }

    /// <inheritdoc />
    public ISettingsFieldEditor? Find(string pageId, string member) =>
        editors.GetValueOrDefault((pageId, member.ToUpperInvariant()));
}
