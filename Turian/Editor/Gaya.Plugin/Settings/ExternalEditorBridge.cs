namespace Gaya.Plugin.Turian;

/// <summary>
/// Keeps the launcher on the editor the preference names: on every settings change, including one a command-line tool
/// wrote, so a new choice applies at once. It also gives the External Editor row its custom editor.
/// </summary>
/// <param name="preference">The live preference page.</param>
/// <param name="launcher">The launcher whose selection follows it.</param>
/// <param name="settings">Raises <see cref="IEditorSettings.Changed"/> when a value is edited or reloaded.</param>
/// <param name="editors">Where the row's editor is registered; <c>null</c> when the host has no custom editors.</param>
/// <param name="editor">The row's editor.</param>
/// <param name="registry">Raises <see cref="IExternalEditorRegistry.Changed"/> when an editor appears or goes.</param>
sealed class ExternalEditorBridge(ExternalEditorSettings preference, ExternalEditorLauncher launcher,
    IEditorSettings settings, ISettingsFieldEditors? editors, ExternalEditorFieldEditor editor,
    IExternalEditorRegistry registry) : IDisposable
{
    /// <summary>Registers the row's editor and applies the stored choice.</summary>
    public void Start()
    {
        editors?.Register(ExternalEditorSettings.PageId, ExternalEditorSettings.Member, editor);
        settings.Changed += Apply;
        registry.Changed += editor.Invalidate;
        Apply();
    }

    void Apply() => launcher.Selection = preference.Editor.ToSelection();

    /// <inheritdoc />
    public void Dispose()
    {
        settings.Changed -= Apply;
        registry.Changed -= editor.Invalidate;
    }
}
