namespace Gaya.Plugin.Turian;

/// <summary>
/// Asks what to do with unsaved edits before a document closes or the studio exits: save them, discard
/// them, or cancel. Renders in the workbench overlay slot.
/// </summary>
sealed class UnsavedChangesDialogChrome(StudioLocalization localization) : IChromeItem
{
    const float dialogWidth = 440f;
    const float bodyHeight = 72f;
    const float buttonWidth = 110f;

    static ThemeTokens Theme => ThemeTokens.Current;

    bool isOpen;
    string message = string.Empty;
    Action<UnsavedChanges>? decided;
    Action? cancelled;

    /// <summary>
    /// Opens the dialog. <paramref name="onDecided"/> runs when the user saves or discards; cancelling, by the button
    /// or by dismissing the dialog, runs <paramref name="onCancelled"/>.
    /// </summary>
    /// <param name="question">The question shown, already localized.</param>
    /// <param name="onDecided">What to do with the user's choice.</param>
    /// <param name="onCancelled">What to do when the user cancels, or null for nothing.</param>
    public void Ask(string question, Action<UnsavedChanges> onDecided, Action? onCancelled = null)
    {
        Cancel();
        message = question;
        decided = onDecided;
        cancelled = onCancelled;
        isOpen = true;
    }

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        gui.Dialog(ref isOpen, localization.T("Unsaved Changes"), () => Body(gui), () => Footer(gui))
            .Width(Theme.Scale(dialogWidth)).Height(Theme.Scale(bodyHeight) + gui.ControlStyle.FieldHeight * 2.5f + 4f);

        if (!isOpen)
        {
            Cancel();
        }
    }

    void Cancel()
    {
        var callback = cancelled;
        decided = null;
        cancelled = null;
        callback?.Invoke();
    }

    void Body(Gui gui)
    {
        using (gui.Node().Expand().AlignContentY(0.5f).Enter())
        {
            gui.DrawText(message, Theme.Text(13f), Theme.Ink, centerInRect: false);
        }
    }

    void Footer(Gui gui)
    {
        using (gui.Node().ExpandWidth().Direction(Axis.Horizontal).Gap(Theme.Scale(8f)).Enter())
        {
            if (gui.Button(localization.T("Save")).Width(Theme.Scale(buttonWidth)).Height(Theme.Scale(28f)).Clicked)
            {
                Decide(UnsavedChanges.Save);
            }

            if (gui.Button(localization.T("Don't Save")).Width(Theme.Scale(buttonWidth)).Height(Theme.Scale(28f)).Clicked)
            {
                Decide(UnsavedChanges.Discard);
            }

            if (gui.Button(localization.T("Cancel")).Width(Theme.Scale(buttonWidth)).Height(Theme.Scale(28f)).Clicked)
            {
                isOpen = false;
            }
        }
    }

    void Decide(UnsavedChanges choice)
    {
        var callback = decided;
        decided = null;
        cancelled = null;
        isOpen = false;
        callback?.Invoke(choice);
    }
}
