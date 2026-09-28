namespace Gaya.Plugin.Turian;

/// <summary>Contextual drawers for references, live collections and nested objects.</summary>
static class StructuralPropertyDrawers
{
    public static IPropertyDrawer Reference(ReferenceDrawer references) => new ReferencePropertyDrawer(references);

    public static IPropertyDrawer Collection(CollectionField collection, ReferenceDrawer? references,
        ISet<string>? collapsed) => new CollectionPropertyDrawer(collection, references, collapsed);

    public static IPropertyDrawer Nested(object target, ReferenceDrawer? references,
        ISet<string>? collapsed) => new NestedPropertyDrawer(target, references, collapsed);

    sealed class ReferencePropertyDrawer(ReferenceDrawer references) : IPropertyDrawer
    {
        public void Draw(Gui gui, FormField field, string id) =>
            FieldDrawers.Row(gui, field.Label, id, () => references.TryDraw(gui, field, id));

        public bool DrawValue(Gui gui, FormField field, string id) => false;
    }

    sealed class CollectionPropertyDrawer(
        CollectionField collection, ReferenceDrawer? references, ISet<string>? collapsed) : IPropertyDrawer
    {
        public void Draw(Gui gui, FormField field, string id) =>
            FieldDrawers.DrawCollection(gui, collection, id, references, collapsed);

        public bool DrawValue(Gui gui, FormField field, string id) => false;
    }

    sealed class NestedPropertyDrawer(
        object target, ReferenceDrawer? references, ISet<string>? collapsed) : IPropertyDrawer
    {
        public void Draw(Gui gui, FormField field, string id) =>
            FieldDrawers.DrawNested(gui, field, target, id, references, collapsed);

        public bool DrawValue(Gui gui, FormField field, string id) => false;
    }
}
