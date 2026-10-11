namespace Gaya.Plugin.Turian;

/// <summary>
/// What the player sees: the edited scene's primary camera while stopped, the running session once
/// Play starts. All of it lives in <see cref="GameViewport"/>; the panel is the dock tab around it.
/// </summary>
sealed class GamePanel(GameViewport viewport) : IPanel, IDisposable
{
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
