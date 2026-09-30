namespace Turian.Engine.UI;

/// <summary>
/// Places a Guinevere UI in a scene. The UI comes from a <c>.ui</c> <see cref="Document"/> asset,
/// or — for user scripts — from an <see cref="OnBuild"/> delegate set in code. The component
/// carries the document reference plus placement and scaling settings; a <see cref="UiManager"/>
/// discovers it by walking the scene each frame.
/// </summary>
[DisallowMultipleComponent]
[ComponentContextMenu("UI/UI Document")]
[TypeId("a3000001-0000-4000-8000-000000000020")]
public class UiDocumentComponent : Component
{
    /// <summary>The <c>.ui</c> document this panel renders. Empty when the UI is supplied via <see cref="OnBuild"/>.</summary>
    public AssetReference<UiDocumentAsset> Document { get; set; } = new();

    /// <summary>
    /// Extra <c>.uss</c> stylesheets applied on top of the ones the document declares with
    /// <c>&lt;Style src&gt;</c>, lowest priority first.
    /// </summary>
    public List<AssetReference<UiStyleSheetAsset>> StyleSheets { get; set; } = [];

    /// <summary>Where the UI is drawn. Defaults to a full-screen overlay.</summary>
    public UiRenderMode Mode { get; set; } = UiRenderMode.ScreenSpaceOverlay;

    /// <summary>
    /// Draw order among screen-space panels: lower values are composited first (further back).
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>How the panel's logical size tracks the framebuffer size (screen-space modes).</summary>
    public UiScaleMode ScaleMode { get; set; } = UiScaleMode.ConstantPixelSize;

    /// <summary>Design resolution for <see cref="UiScaleMode.ScaleWithScreenSize"/>.</summary>
    public Int2 ReferenceResolution { get; set; } = new(1920, 1080);

    /// <summary>Render-target size in pixels for <see cref="UiRenderMode.WorldSpace"/>.</summary>
    public Int2 PanelSize { get; set; } = new(1024, 640);

    /// <summary>
    /// Builds the UI each frame. Invoked once per layout pass and once per render pass, so it must
    /// be idempotent (the standard immediate-mode contract). Not serialized — set it in code.
    /// </summary>
    [JsonIgnore]
    public Action<Gui>? OnBuild { get; set; }
}
