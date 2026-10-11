namespace Turian.Engine.UI;

/// <summary>Displays rolling game frame statistics from an explicitly authored scene component during Play.</summary>
[ComponentContextMenu("UI/Frame Statistics HUD")]
[TypeId("93710463-ea78-4e80-b73a-1811ce9a5459")]
public sealed class FrameStatisticsHudComponent : UiDocumentComponent, IRenderStatisticsTarget
{
    FrameStatisticsDrawing? drawing;

    /// <summary>Enables the HUD and its frame collection while the game is playing.</summary>
    public bool ShowHud
    {
        get;
        set
        {
            field = value;
            if (!value) Statistics.Clear();
            if (IsStarted) OnBuild = value ? Build : null;
        }
    } = true;

    /// <summary>HUD position in logical pixels from the game's upper left corner.</summary>
    public Vector2 HudPosition { get; set; } = new(12, 12);

    /// <summary>HUD size in logical pixels.</summary>
    public Vector2 HudSize { get; set; } = new(400, 122);

    /// <summary>Text size in logical pixels.</summary>
    public float FontSize { get; set; } = 11;

    /// <inheritdoc />
    [JsonIgnore, Hide]
    public override bool PlayModeOnly => true;

    /// <inheritdoc />
    [JsonIgnore, Hide]
    public bool IsRecording => IsStarted && IsActive && ShowHud;

    /// <inheritdoc />
    [JsonIgnore, Hide]
    public FrameStatistics Statistics { get; } = new();

    /// <inheritdoc />
    [JsonIgnore, Hide]
    public double? SceneLoadMilliseconds { get; set; }

    /// <inheritdoc />
    public override void OnStart() => OnBuild = ShowHud ? Build : null;

    void Build(Gui gui)
    {
        if (!IsRecording || Statistics.Count == 0) return;
        using (gui.Node(HudSize.X, HudSize.Y, "frame-statistics").Absolute(HudPosition.X, HudPosition.Y).Enter())
        {
            gui.SetZIndex(SortOrder);
            if (gui.Pass != Pass.Pass2Render) return;
            drawing ??= new FrameStatisticsDrawing(this);
            gui.CurrentNode.DrawList.Add(drawing);
        }
    }

    /// <inheritdoc />
    public override void OnDisable() => Statistics.Clear();

    /// <inheritdoc />
    public override void OnDestroy()
    {
        OnBuild = null;
        drawing?.Dispose();
        drawing = null;
    }
}
