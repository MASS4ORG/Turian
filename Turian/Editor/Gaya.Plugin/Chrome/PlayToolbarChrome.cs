namespace Gaya.Plugin.Turian;

/// <summary>
/// The play controls, sharing the menu bar's row so they cost no height of their own: play/stop,
/// pause/resume and step. Every button runs the same command its File-menu entry does, through
/// <see cref="ICommandDispatcher"/>, so the two can never disagree about what Play means.
/// </summary>
sealed class PlayToolbarChrome(ICommandDispatcher commands, PlayModeService playMode) : IChromeItem
{
    static ThemeTokens Theme => ThemeTokens.Current;

    static float ButtonSize => Theme.Scale(24f);
    static float IconSize => Theme.Scale(13f);

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);
        if (playMode.State == PlayState.Playing) gui.RequestFrame();

        var playing = playMode.IsActive;
        var paused = playMode.State == PlayState.Paused;

        using (gui.Node(-1, ButtonSize, "play/buttons").Direction(Axis.Horizontal).Gap(3f)
                   .Padding(8f, 0f).ContentAlignY(0.5f).Enter())
        {
            Button(gui, "play", playing ? Icons.PlayStop : Icons.PlayStart, "gaya.turian.play", playing);
            Button(gui, "startup", Icons.PlayRestart, "gaya.turian.playStartupScene", false);
            Button(gui, "pause", paused ? Icons.PlayStart : Icons.PlayPause, "gaya.turian.playPause", paused);
            Button(gui, "step", Icons.PlayStep, "gaya.turian.playStep", false);
        }
    }

    void Button(Gui gui, string id, string icon, string commandId, bool active)
    {
        using (gui.Node(ButtonSize, ButtonSize, $"play/{id}").ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var enabled = commands.CanExecute(commandId);
            var interactable = gui.GetInteractable();
            var hot = gui.Pass == Pass.Pass2Render && enabled && interactable.OnHover();

            if (active) gui.DrawBackgroundRect(Theme.AccentFill, 3f);
            else if (hot) gui.DrawBackgroundRect(Theme.Hover, 3f);

            gui.ThemedIcon(icon, IconSize, enabled ? 1f : 0.4f);

            if (hot && interactable.OnClick()) commands.Execute(commandId);
        }
    }
}
