namespace Gaya.Plugin.Turian;

/// <summary>
/// What the player sees in one scene tab: that scene's primary camera while stopped, the running session once Play
/// started from this tab. Another tab's Game panel only names the tab the game plays in. All rendering lives in
/// <see cref="GameViewport"/>; the panel is the dock tab around it.
/// </summary>
sealed class GamePanel(GameViewport viewport, IDocument document, PlayingDocument playing, PlayModeService playMode,
    StudioLocalization localization) : IPanel, ISuspendablePanel, IDisposable
{
    bool suspendDeferred;
    bool watching;

    /// <summary>The "playing in" message while another tab owns the running game, otherwise null.</summary>
    internal string? PlaceholderText => playing.IsPlayingElsewhere(document.Id)
        ? string.Format(CultureInfo.CurrentCulture, localization.T("Playing in {0}"), playing.Title)
        : null;

    /// <inheritdoc />
    public void RenderHeader(Gui gui, PanelHeaderContext context)
    {
        var theme = ThemeTokens.Current;
        using (gui.Node(theme.Scale(48), -1, $"{context.PanelId}/stats").ExpandHeight().BlockInput()
                   .AlignContentX(0.5f).AlignContentY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            if (gui.Pass == Pass.Pass2Render && interactable.OnHover())
            {
                gui.DrawBackgroundRect(theme.Hover);
            }

            gui.DrawText("Stats", theme.Text(11), viewport.ShowStatistics ? theme.Accent : theme.InkDim);
            if (gui.Pass == Pass.Pass2Render && interactable.OnClick())
            {
                viewport.ShowStatistics = !viewport.ShowStatistics;
            }
        }
    }

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        suspendDeferred = false;
        using (gui.Node().Expand().Enter())
        {
            if (PlaceholderText is { } text)
            {
                using (gui.Node(-1, -1, "game/playingElsewhere").Expand().AlignContentX(0.5f).AlignContentY(0.5f).Enter())
                {
                    gui.DrawText(text, ThemeTokens.Current.Text(12), ThemeTokens.Current.InkDim);
                }

                return;
            }

            viewport.Render(gui);
        }
    }

    /// <summary>
    /// Releases the viewport's render target, except while the game plays in this tab: that one keeps rendering, and is
    /// released when play ends.
    /// </summary>
    public void Suspend()
    {
        if (playing.IsPlayingIn(document.Id))
        {
            suspendDeferred = true;
            if (!watching)
            {
                watching = true;
                playMode.StateChanged += OnPlayStateChanged;
            }

            return;
        }

        viewport.Suspend();
    }

    void OnPlayStateChanged(PlayState state)
    {
        if (state != PlayState.Stopped)
        {
            return;
        }

        playMode.StateChanged -= OnPlayStateChanged;
        watching = false;
        if (suspendDeferred)
        {
            suspendDeferred = false;
            viewport.Suspend();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        playMode.StateChanged -= OnPlayStateChanged;
        viewport.Dispose();
    }
}
