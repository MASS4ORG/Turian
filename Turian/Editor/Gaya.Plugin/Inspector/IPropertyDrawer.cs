namespace Gaya.Plugin.Turian;

/// <summary>Draws a field's complete presentation or its value when the caller owns the label.</summary>
public interface IPropertyDrawer
{
    /// <summary>Draws the labelled field.</summary>
    void Draw(Gui gui, FormField field, string id);

    /// <summary>Draws only the value; returns false when no value-only form is available.</summary>
    bool DrawValue(Gui gui, FormField field, string id);

    /// <summary>Draws a value with optional translation for enum labels.</summary>
    bool DrawValue(Gui gui, FormField field, string id, Func<string, string>? translate) =>
        DrawValue(gui, field, id);
}
