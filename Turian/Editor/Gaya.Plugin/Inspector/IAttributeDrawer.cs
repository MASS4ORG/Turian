namespace Gaya.Plugin.Turian;

/// <summary>Wraps a field's renderer without replacing its value-type drawer.</summary>
public interface IAttributeDrawer
{
    /// <summary>Lower orders wrap higher orders; ties retain attribute declaration order.</summary>
    int Order => 0;

    /// <summary>Draws the decoration and invokes <paramref name="next"/> for the inner field.</summary>
    void Draw(Gui gui, FormField field, Attribute attribute, string id, Action next);
}
