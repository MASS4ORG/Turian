namespace Gaya.Plugin.Turian;

/// <summary>
/// Asks the user to confirm an action, such as unpacking a prefab instance before deleting one of its objects.
/// Renders in the workbench overlay slot.
/// </summary>
sealed class ConfirmDialogChrome(StudioLocalization localization) : IChromeItem
{
    const float dialogWidth = 460f;
    const float bodyHeight = 72f;
    const float buttonWidth = 150f;

    static ThemeTokens Theme => ThemeTokens.Current;

    bool isOpen;
    string title = string.Empty;
    string message = string.Empty;
    string confirmLabel = string.Empty;
    Action? confirmed;

    /// <summary>Opens the dialog; <paramref name="onConfirm"/> runs only when the user confirms.</summary>
    /// <param name="dialogTitle">The title, already localized.</param>
    /// <param name="question">The question, already localized.</param>
    /// <param name="confirm">The confirming button's label, already localized.</param>
    /// <param name="onConfirm">What confirming does.</param>
    public void Ask(string dialogTitle, string question, string confirm, Action onConfirm)
    {
        title = dialogTitle;
        message = question;
        confirmLabel = confirm;
        confirmed = onConfirm;
        isOpen = true;
    }

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        gui.Dialog(ref isOpen, title, () => Body(gui), () => Footer(gui))
            .Width(Theme.Scale(dialogWidth)).Height(Theme.Scale(bodyHeight) + gui.ControlStyle.FieldHeight * 2.5f + 4f);

        if (!isOpen)
        {
            confirmed = null;
        }
    }

    void Body(Gui gui)
    {
        using (gui.Node().Expand().AlignContentY(0.5f).Enter())
        {
            gui.DrawText(message, Theme.Text(13f), Theme.Ink, centerInRect: false,
                wrapWidth: Theme.Scale(dialogWidth - 40f));
        }
    }

    void Footer(Gui gui)
    {
        using (gui.Node().ExpandWidth().Direction(Axis.Horizontal).Gap(Theme.Scale(8f)).Enter())
        {
            if (gui.Button(confirmLabel).Width(Theme.Scale(buttonWidth)).Height(Theme.Scale(28f)).Clicked)
            {
                var callback = confirmed;
                confirmed = null;
                isOpen = false;
                callback?.Invoke();
            }

            if (gui.Button(localization.T("Cancel")).Width(Theme.Scale(buttonWidth)).Height(Theme.Scale(28f)).Clicked)
            {
                isOpen = false;
            }
        }
    }
}
