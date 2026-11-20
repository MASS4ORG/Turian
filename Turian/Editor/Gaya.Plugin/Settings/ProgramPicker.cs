namespace Gaya.Plugin.Turian;

/// <summary>Asks the user for a program, so the External Editor row does not depend on a particular dialog.</summary>
interface IProgramPicker
{
    /// <summary>Opens the picker.</summary>
    /// <param name="title">What is being asked for.</param>
    /// <param name="onComplete">Receives the chosen path, or <c>null</c> when the user cancelled.</param>
    void Pick(string title, Action<string?> onComplete);
}

/// <summary>Asks for a program through the studio's file dialog.</summary>
/// <param name="dialogs">The studio's file dialog.</param>
sealed class FileDialogProgramPicker(FileDialogChrome dialogs) : IProgramPicker
{
    /// <inheritdoc />
    public void Pick(string title, Action<string?> onComplete) =>
        dialogs.Show(new FileDialogRequest
        {
            Mode = FileDialogMode.OpenFile,
            Title = title,
            Filters = [],
            OnComplete = onComplete,
        });
}
