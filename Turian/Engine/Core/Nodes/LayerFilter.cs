namespace Turian.Engine.Core;

/// <summary>Resolves layer identities from the project's current settings and filters masks by layer space.</summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class LayerFilter(IAppSettings? project = null, SettingsService? settingsService = null)
{
    readonly TagsAndLayersSettings defaults = new();
    readonly HashSet<(bool Physics, int Index)> warned = [];

    /// <summary>The settings DataAsset currently in force.</summary>
    public TagsAndLayersSettings Settings =>
        (settingsService?.Settings ?? project)?.Get<TagsAndLayersSettings>() ?? defaults;

    /// <summary>Resolves a physics index, falling back to Default and warning once for an orphaned index.</summary>
    public LayerSlot ResolvePhysics(int index) => Resolve(index, physics: true);

    /// <summary>Resolves a rendering index, falling back to Default and warning once for an orphaned index.</summary>
    public LayerSlot ResolveRender(int index) => Resolve(index, physics: false);

    /// <summary>Whether a mask accepts a node's physics layer.</summary>
    public bool IncludesPhysics(Node node, LayerMask mask) => mask.Contains(ResolvePhysics(node.PhysicsLayer).Index);

    /// <summary>Whether a mask accepts a node's rendering layer.</summary>
    public bool IncludesRender(Node node, LayerMask mask) => mask.Contains(ResolveRender(node.RenderLayer).Index);

    LayerSlot Resolve(int index, bool physics)
    {
        var slot = physics ? Settings.FindPhysicsLayer(index) : Settings.FindRenderLayer(index);
        if (slot is not null) return slot;
        if (warned.Add((physics, index)))
            Log.Logger.LogWarning("{Space} layer index {Index} is unnamed or missing; migrating to Default (0)",
                physics ? "Physics" : "Render", index);
        return (physics ? Settings.FindPhysicsLayer(0) : Settings.FindRenderLayer(0))
               ?? TagsAndLayersSettings.DefaultLayer(physics);
    }
}
