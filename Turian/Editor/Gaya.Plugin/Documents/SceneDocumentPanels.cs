namespace Gaya.Plugin.Turian;

/// <summary>
/// Registers the scene document kind and the three panels bound to it, Scene Tree, Scene and Game, each built per open
/// scene tab with that tab's document.
/// </summary>
static class SceneDocumentPanels
{
    static readonly string[] Kinds = [SceneDocument.KindId];

    /// <summary>Contributes the kind and its panels.</summary>
    /// <param name="context">The registration surface handed to the plugin.</param>
    public static void Register(IPluginContext context)
    {
        context.DocumentKinds.Register(SceneDocument.SceneKind);
        context.DocumentKinds.Register(AssetDocument.AssetKind);

        context.Panels.Register(PanelDescriptor.ForDocuments(
            GayaPlugin.SceneTreePanelId, "Scene Tree", PanelPlacement.Left, Kinds,
            static (sp, _) => new SceneTreePanel(
                sp.GetRequiredService<SceneTreeController>(),
                sp.GetRequiredService<NodeInspectorController>(),
                sp.GetRequiredService<AssetManager>(),
                sp.GetRequiredService<PrefabAuthoring>(),
                sp.GetRequiredService<PrefabStage>(),
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<UndoService>(),
                sp.GetRequiredService<PrefabOverrideOperations>(),
                sp.GetRequiredService<ConfirmDialogChrome>(),
                sp.GetRequiredService<StudioLocalization>(),
                sp.GetRequiredService<AssetDatabase>())));

        context.Panels.Register(PanelDescriptor.ForDocuments(
            GayaPlugin.ViewportPanelId, "Scene", PanelPlacement.Center, Kinds,
            static (sp, document) =>
            {
                var documents = sp.GetRequiredService<IDocumentService>();
                var tree = sp.GetRequiredService<SceneTreeController>();
                var inspector = sp.GetRequiredService<NodeInspectorController>();
                return new ScenePanel(
                    new SceneViewport(
                        sp.GetRequiredService<Vulkan>(),
                        sp.GetRequiredService<AssetDatabase>(),
                        tree,
                        inspector,
                        sp.GetRequiredService<GizmoDrawerCatalog>(),
                        sp.GetRequiredService<PlayModeService>(),
                        sp.GetRequiredService<EditorCameraSettings>(),
                        sp.GetRequiredService<ILogger>(),
                        sp.GetRequiredService<UndoService>(),
                        sp.GetRequiredService<LocaleService>(),
                        isActive: () => ReferenceEquals(documents.Active, document)),
                    tree,
                    inspector,
                    (SceneDocument)document,
                    sp.GetRequiredService<PrefabStage>());
            }));

        context.Panels.Register(PanelDescriptor.ForDocuments(
            GayaPlugin.GamePanelId, "Game", PanelPlacement.Center, Kinds,
            static (sp, document) =>
            {
                var playMode = sp.GetRequiredService<PlayModeService>();
                return new GamePanel(
                    new GameViewport(
                        sp.GetRequiredService<Vulkan>(),
                        sp.GetRequiredService<AssetDatabase>(),
                        sp.GetRequiredService<SceneTreeController>(),
                        playMode,
                        sp.GetRequiredService<ILogger>(),
                        sp.GetRequiredService<EditorCameraSettings>()),
                    document,
                    sp.GetRequiredService<PlayingDocument>(),
                    playMode,
                    sp.GetRequiredService<StudioLocalization>());
            }));
    }
}
