namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws a user-code <c>[Panel]</c> object the way the Settings panel draws a settings page: its
/// public members reflected into a form, its <c>[Button]</c> methods drawn as actions below them. A
/// panel author writes a plain class, not a Guinevere control.
/// </summary>
sealed class UserPanel(UserPanelPage page) : IPanel
{
    readonly FormRenderContext context = new()
    {
        Drawers = TurianForms.Drawers,
        CanInline = TurianForms.CanInline,
    };

    FormModel? model;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(ThemeTokens.Current.Scale(8f))
                   .Padding(ThemeTokens.Current.Scale(12f)).Enter())
        {
            TurianForms.ApplyStyle(gui);
            gui.ScrollY();

            model ??= InspectorForms.Build(page.Target);
            var fields = model.Sections.SelectMany(section => section.BodyFields).ToList();

            for (var i = 0; i < fields.Count; i++)
                gui.FormField(fields[i], $"userpanel/{page.Id}/field{i}", context);

            foreach (var section in model.Sections)
                for (var b = 0; b < section.Buttons.Count; b++)
                {
                    var button = section.Buttons[b];
                    if (FormControls.TextButton(gui, button.Label, $"userpanel/{page.Id}/button{b}"))
                        button.Invoke();
                }
        }
    }
}
