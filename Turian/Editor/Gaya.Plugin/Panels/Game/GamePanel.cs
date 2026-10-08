namespace Gaya.Plugin.Turian;

/// <summary>
/// What the player sees: the edited scene's primary camera while stopped, the running session once
/// Play starts. All of it lives in <see cref="GameViewport"/>; the panel is the dock tab around it.
/// </summary>
sealed class GamePanel(GameViewport viewport) : IPanel, IDisposable
{
    /// <inheritdoc />
    public void RenderHeader(Gui gui, PanelHeaderContext context)
    {
        var theme = ThemeTokens.Current;
        using (gui.Node(theme.Scale(48), -1, $"{context.PanelId}/stats").ExpandHeight().BlockInput()
                   .ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            if (gui.Pass == Pass.Pass2Render && interactable.OnHover()) gui.DrawBackgroundRect(theme.Hover);
            gui.DrawText("Stats", theme.Text(11), viewport.ShowStatistics ? theme.Accent : theme.InkDim);
            if (gui.Pass == Pass.Pass2Render && interactable.OnClick())
                viewport.ShowStatistics = !viewport.ShowStatistics;
        }
    }

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        using (gui.Node().Expand().Enter())
            viewport.Render(gui);
    }

    /// <inheritdoc />
    public void Dispose() => viewport.Dispose();
}
