namespace Gaya.Plugin.Turian;

sealed class OpenCSharpProjectChrome(ICommandDispatcher commands, StudioLocalization localization) : IChromeItem
{
    /// <inheritdoc />
    public void Render(Gui gui)
    {
        const string commandId = StudioCommands.OpenCSharpProjectId;
        var theme = ThemeTokens.Current;
        var size = theme.Scale(24f);
        using (gui.Node(size, size, "open-csharp-project").AlignContentX(0.5f).AlignContentY(0.5f).Enter())
        {
            var enabled = commands.CanExecute(commandId);
            var interaction = gui.GetInteractable();
            var hot = gui.Pass == Pass.Pass2Render && enabled && interaction.OnHover();
            if (hot)
            {
                gui.DrawBackgroundRect(theme.Hover, 3f);
            }

            gui.ThemedIcon(Icons.AssetScript, theme.Scale(13f), enabled ? 1f : 0.4f);
            gui.Tooltip(gui.CurrentNode, localization.T(StudioCommands.OpenCSharpProjectLabel));
            if (hot && interaction.OnClick())
            {
                commands.Execute(commandId);
            }
        }
    }
}
