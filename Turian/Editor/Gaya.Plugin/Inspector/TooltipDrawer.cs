namespace Gaya.Plugin.Turian;

/// <summary>Decorates any property drawer with a hover description.</summary>
sealed class TooltipDrawer : IAttributeDrawer
{
    public void Draw(Gui gui, FormField field, Attribute attribute, string id, Action next)
    {
        using (gui.Node(-1, -1, $"{id}/tooltip").ExpandWidth().Direction(Axis.Vertical).Enter())
        {
            next();
            gui.Tooltip(gui.CurrentNode, ((TooltipAttribute)attribute).Text);
        }
    }
}
