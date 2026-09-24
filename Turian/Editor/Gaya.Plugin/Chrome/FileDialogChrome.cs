namespace Gaya.Plugin.Turian;

/// <summary>
/// Hosts the studio's one file dialog. Like <see cref="AboutDialogChrome"/> it draws nothing into the
/// strip it is registered on — it only needs a slot that renders every frame, so a command can raise
/// the dialog through <see cref="Show"/> and have it appear on the next one.
/// </summary>
/// <remarks>
/// A single instance, shared by every caller, because the dialog is modal: raising a second one while
/// the first is up would strand its request. Callers hear the answer through
/// <see cref="FileDialogRequest.OnComplete"/>, which runs mid-render, so whatever it does must be safe
/// to do while the frame is being built — opening a project is, since the project switcher already
/// does exactly that.
/// </remarks>
sealed class FileDialogChrome : IChromeItem
{
    const float dialogWidth = 840f;
    const float dialogHeight = 460f;

    readonly FileDialogState state = new();

    /// <summary>Raises the dialog. Called from the commands that need a path.</summary>
    /// <param name="request">What to ask the user for.</param>
    public void Show(FileDialogRequest request) => state.Open(request);

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        var theme = StudioTheme.Current;
        gui.FileDialog(state, theme.Scale(dialogWidth), theme.Scale(dialogHeight),
            theme.Text(theme.FontSize + 1f));
    }
}
