namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws a user-code <c>[Panel]</c> object the way the Settings panel draws a settings page: its
/// public members reflected into a form, its <c>[Button]</c> methods drawn as actions below them. A
/// panel author writes a plain class, not a Guinevere control.
/// </summary>
sealed class UserPanel(UserPanelPage page) : IPanel
{
    readonly HashSet<string> collapsed = [];
    FormModel? model;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(StudioTheme.Current.Scale(8f))
                   .Padding(StudioTheme.Current.Scale(12f)).Enter())
        {
            gui.ScrollY();

            model ??= FormBuilder.Build(page.Target);
            var fields = model.Sections.SelectMany(section => section.BodyFields).ToList();

            for (var i = 0; i < fields.Count; i++)
                FieldDrawers.Draw(gui, fields[i], $"userpanel/{page.Id}/field{i}", references: null, collapsed);

            foreach (var section in model.Sections)
                for (var b = 0; b < section.Buttons.Count; b++)
                {
                    var button = section.Buttons[b];
                    if (FieldDrawers.TextButton(gui, button.Label, $"userpanel/{page.Id}/button{b}"))
                        button.Invoke();
                }
        }
    }
}
