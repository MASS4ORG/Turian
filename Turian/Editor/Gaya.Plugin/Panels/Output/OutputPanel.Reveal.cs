namespace Gaya.Plugin.Turian;

sealed partial class OutputPanel
{
    /// <summary>
    /// Makes an entry another part of Studio asked for visible: the text filter is cleared and its severity, and
    /// Studio messages when it came from Studio, are switched back on, so the row is in the list to select.
    /// </summary>
    void PrepareReveal(LogLine wanted)
    {
        revealing = wanted;
        filter = "";
        switch (wanted.Level)
        {
            case LogLevel.Critical or LogLevel.Error:
                showErrors = true;
                break;
            case LogLevel.Warning:
                showWarnings = true;
                break;
            case LogLevel.Information:
                showInformation = true;
                break;
            default:
                showLog = true;
                break;
        }

        if (wanted.IsInternal && !settings.ShowStudio)
        {
            settings.ShowStudio = true;
        }
    }

    /// <summary>
    /// Selects the newest row showing the requested entry, follows the tail when it is the last row, and asks for it
    /// to be scrolled into view once layout is final.
    /// </summary>
    void SelectRevealed()
    {
        if (revealing is not { } wanted)
        {
            return;
        }

        revealing = null;
        var index = -1;
        for (var row = rows.Count - 1; row >= 0 && index < 0; row--)
        {
            if (rows[row].Line == wanted)
            {
                index = row;
            }
        }

        // A collapsed row stands for its run of duplicates, so the entry may sit inside one; the newest row is the best guess.
        index = index < 0 ? rows.Count - 1 : index;
        if (index < 0)
        {
            return;
        }

        selection.Set(rows, index);
        selectedIndex = index;
        followTail = index == rows.Count - 1;
        scrollToSelected = true;
    }
}
