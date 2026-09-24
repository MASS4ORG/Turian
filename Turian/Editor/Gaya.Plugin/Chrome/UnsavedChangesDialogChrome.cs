namespace Gaya.Plugin.Turian;

/// <summary>
/// Asks what to do with unsaved edits before a document closes or the studio exits: save them, discard
/// them, or cancel. Draws nothing in the menu bar strip it is registered on — like the About dialog, it
/// only needs a slot that renders every frame.
/// </summary>
sealed class UnsavedChangesDialogChrome(StudioLocalization localization) : IChromeItem
{
    const float dialogWidth = 440f;
    const float bodyHeight = 72f;
    const float footerHeight = 44f;
    const float buttonWidth = 110f;

    static StudioTheme Theme => StudioTheme.Current;

    bool isOpen;
    string message = string.Empty;
    Action<UnsavedChanges>? decided;

    /// <summary>
    /// Opens the dialog. <paramref name="onDecided"/> runs when the user saves or discards; cancelling,
    /// by the button or by dismissing the dialog, runs nothing.
    /// </summary>
    /// <param name="question">The question shown, already localized.</param>
    /// <param name="onDecided">What to do with the user's choice.</param>
    public void Ask(string question, Action<UnsavedChanges> onDecided)
    {
        message = question;
        decided = onDecided;
        isOpen = true;
    }

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        gui.Dialog(ref isOpen, localization.T("Unsaved Changes"), () => Body(gui),
            Theme.Scale(dialogWidth), Theme.Scale(bodyHeight), () => Footer(gui),
            footerHeight: Theme.Scale(footerHeight));

        if (!isOpen) decided = null;
    }

    void Body(Gui gui)
    {
        using (gui.Node().Expand().ContentAlignY(0.5f).Enter())
            gui.DrawText(message, Theme.Text(13f), Theme.Ink, centerInRect: false);
    }

    void Footer(Gui gui)
    {
        using (gui.Node().ExpandWidth().Direction(Axis.Horizontal).Gap(Theme.Scale(8f)).Enter())
        {
            if (gui.Button(localization.T("Save"), width: Theme.Scale(buttonWidth), height: Theme.Scale(28f)))
                Decide(UnsavedChanges.Save);

            if (gui.Button(localization.T("Don't Save"), width: Theme.Scale(buttonWidth), height: Theme.Scale(28f)))
                Decide(UnsavedChanges.Discard);

            if (gui.Button(localization.T("Cancel"), width: Theme.Scale(buttonWidth), height: Theme.Scale(28f)))
                isOpen = false;
        }
    }

    void Decide(UnsavedChanges choice)
    {
        var callback = decided;
        decided = null;
        isOpen = false;
        callback?.Invoke(choice);
    }
}
