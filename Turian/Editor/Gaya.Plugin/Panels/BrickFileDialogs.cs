namespace Gaya.Plugin.Turian;

/// <summary>Asks for a brick folder or <c>.brick</c> file through the studio's file dialog.</summary>
/// <param name="dialogs">The studio's file dialog.</param>
sealed class BrickFileDialogs(FileDialogChrome dialogs) : IBrickFileDialogs
{
    /// <inheritdoc />
    public void Pick(bool folder, string title, Action<string?> onComplete) =>
        dialogs.Show(new FileDialogRequest
        {
            Mode = folder ? FileDialogMode.SelectFolder : FileDialogMode.OpenFile,
            Title = title,
            Filters = folder ? [] : [FileDialogFilter.Of("Bricks", ".brick")],
            OnComplete = onComplete,
        });
}
