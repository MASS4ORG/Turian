namespace Gaya.Host;

/// <summary>
/// Holds the committed theme and the user's size scales, and publishes the combination through
/// <see cref="StudioTheme.Current"/> so every panel draws with it.
/// </summary>
/// <remarks>
/// A preview is deliberately not sticky: <see cref="Preview"/> marks the frame it was called on, and
/// <see cref="EndFrame"/> drops anything that was not renewed. A menu row therefore previews by
/// calling it while hovered and needs no "stopped hovering" event of its own.
/// </remarks>
public sealed class ThemeService : IThemeService
{
    readonly List<StudioTheme> themes = [.. StudioTheme.BuiltIn];

    StudioTheme committed = StudioTheme.Dark;
    StudioTheme? preview;
    bool previewRenewed;
    float textSize = StudioTheme.Dark.FontSize;
    float zoom = 1f;

    /// <summary>Creates the service with the built-in themes and publishes the default.</summary>
    public ThemeService() => Publish();

    /// <inheritdoc />
    public IReadOnlyList<StudioTheme> Themes => themes;

    /// <inheritdoc />
    public StudioTheme Current { get; private set; } = StudioTheme.Dark;

    /// <inheritdoc />
    public string CommittedName => committed.Name;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Register(StudioTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var existing = themes.FindIndex(registered => Same(registered.Name, theme.Name));
        if (existing >= 0) themes[existing] = theme;
        else themes.Add(theme);
    }

    /// <inheritdoc />
    public void Apply(string name)
    {
        if (Find(name) is not { } theme) return;
        if (ReferenceEquals(committed, theme) && preview is null) return;

        committed = theme;
        preview = null;
        Publish();
    }

    /// <inheritdoc />
    public void Preview(string name)
    {
        previewRenewed = true;

        if (Find(name) is not { } theme || ReferenceEquals(preview, theme)) return;

        preview = theme;
        Publish();
    }

    /// <inheritdoc />
    public void SetScale(float requestedTextSize, float requestedZoom)
    {
        if (Math.Abs(requestedTextSize - textSize) < 0.001f
            && Math.Abs(requestedZoom - zoom) < 0.001f) return;

        textSize = requestedTextSize;
        zoom = requestedZoom;
        Publish();
    }

    /// <summary>
    /// Drops a preview no one renewed this frame. Called by the workbench once per drawn frame.
    /// </summary>
    public void EndFrame()
    {
        if (previewRenewed)
        {
            previewRenewed = false;
            return;
        }

        if (preview is null) return;

        preview = null;
        Publish();
    }

    StudioTheme? Find(string name) => themes.FirstOrDefault(theme => Same(theme.Name, name));

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    void Publish()
    {
        var theme = preview ?? committed;
        Current = theme with { TextScale = textSize / theme.FontSize, Zoom = zoom };
        StudioTheme.Current = Current;
        Changed?.Invoke();
    }
}
