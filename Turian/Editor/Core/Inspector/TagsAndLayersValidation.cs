namespace Turian.Editor.Core;

/// <summary>Inspector diagnostics for settings and nodes with unnamed layer indices.</summary>
public static class TagsAndLayersValidation
{
    /// <summary>Returns warnings for the inspected object using the project's current layer definitions.</summary>
    public static IReadOnlyList<string> Warnings(object target, TagsAndLayersSettings settings) => target switch
    {
        TagsAndLayersSettings layers => layers.Validate(),
        Node node => NodeWarnings(node, settings),
        _ => [],
    };

    static IReadOnlyList<string> NodeWarnings(Node node, TagsAndLayersSettings settings)
    {
        var warnings = new List<string>();
        if (settings.FindPhysicsLayer(node.PhysicsLayer) is null)
            warnings.Add($"Physics layer {node.PhysicsLayer} is unnamed; it resolves to Default when loaded.");
        if (settings.FindRenderLayer(node.RenderLayer) is null)
            warnings.Add($"Render layer {node.RenderLayer} is unnamed; it resolves to Default when loaded.");
        return warnings;
    }
}
