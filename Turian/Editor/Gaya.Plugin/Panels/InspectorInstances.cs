namespace Gaya.Plugin.Turian;

/// <summary>
/// Registers Inspector panel instances: the one the studio starts with, and any more the user opens
/// from the menu. Each is a fully independent <see cref="InspectorPanel"/> — its own fields, its own
/// fold state, its own lock — the way Unity lets several Inspector windows coexist.
/// </summary>
sealed class InspectorInstances(IPanelRegistry panels, ITabStripChromeRegistry tabStripChrome)
{
    int count;

    /// <summary>Registers the Inspector the studio always starts with.</summary>
    public void RegisterInitial(string panelId) => Register(panelId, "Inspector");

    /// <summary>Registers one more Inspector instance and returns its id, for the workbench to show.</summary>
    public string CreateAdditional()
    {
        count++;
        var id = $"gaya.turian.inspector.extra{count}";
        Register(id, $"Inspector {count + 1}");
        return id;
    }

    void Register(string panelId, string title)
    {
        panels.Register(new PanelDescriptor(panelId, title, PanelPlacement.Right,
            sp => new InspectorPanel(
                sp.GetRequiredService<NodeInspectorController>(),
                sp.GetRequiredService<AssetManager>(),
                sp.GetRequiredService<ReferencePicker>(),
                sp.GetRequiredService<AssetRevealService>(),
                sp.GetRequiredService<AssetInspectionService>(),
                sp.GetRequiredService<InspectorSettings>(),
                sp.GetRequiredService<Vulkan>(),
                sp.GetRequiredService<AssetPreviewCatalog>())));

        tabStripChrome.Register(new TabStripChromeDescriptor($"{panelId}.tabMenu", panelId,
            sp => new InspectorTabChrome(
                sp.GetRequiredService<IPanelAccessor>(),
                sp.GetRequiredService<IEditorSettings>(),
                sp.GetRequiredService<InspectorSettings>(),
                panelId)));
    }
}
