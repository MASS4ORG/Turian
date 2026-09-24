namespace Gaya.Host;

/// <summary>
/// Where a panel sat when it left the layout without the user closing it — its plugin unloaded, or a
/// user assembly was still compiling at startup — so it can return there once it registers again.
/// </summary>
/// <param name="Zone">The zone of the tab group it was in.</param>
/// <param name="TabbedWith">A panel it shared a tab group with, or null when it was alone.</param>
/// <param name="FloatBounds">The floating window it was in, or null when it was docked.</param>
public sealed record ParkedPanel(DockZone Zone, string? TabbedWith = null, Rect? FloatBounds = null);

/// <summary>What the workbench persists: the dock tree and what it knows about the panels outside it.</summary>
/// <param name="Layout">The dock arrangement.</param>
/// <param name="KnownPanels">
/// Every panel the layout has accounted for. A known panel missing from the tree was closed and stays
/// closed; only unknown ones are opened at their declared placement.
/// </param>
/// <param name="ParkedPanels">Panels waiting to come back to where they were.</param>
public sealed record WorkbenchLayoutState(
    DockLayout Layout,
    HashSet<string> KnownPanels,
    Dictionary<string, ParkedPanel> ParkedPanels);

/// <summary>
/// Persists the workbench's <see cref="DockLayout"/> between runs. The file is a convenience, never a
/// requirement: anything unreadable, from another schema version, or naming panels no plugin
/// contributes is discarded and the layout is re-seeded from the registry.
/// </summary>
public sealed class WorkbenchLayoutStore(ILogger log, string? path = null)
{
    static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Where the layout is written: <c>~/.gaya/layout.json</c> unless the
    /// caller supplies its own path.
    /// </summary>
    public string Path { get; } = path ?? UserConfigPath.For("layout.json");

    /// <summary>
    /// Reads the saved layout, or returns null when there is nothing usable to read. A file holding
    /// only a dock tree, as earlier versions wrote, comes back with no known panels.
    /// </summary>
    public WorkbenchLayoutState? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;

            var text = File.ReadAllText(Path);
            if (JsonNode.Parse(text) is not JsonObject root) return null;

            if (root["layout"] is not { } layoutNode)
                return DockLayout.FromJson(text) is { } bare
                    ? new WorkbenchLayoutState(bare, [], [])
                    : null;

            if (DockLayout.FromJson(layoutNode.ToJsonString()) is not { } layout) return null;

            var known = root["known"]?.Deserialize<HashSet<string>>(jsonOptions) ?? [];
            known.UnionWith(layout.PanelIds);
            var parked = root["parked"]?.Deserialize<Dictionary<string, ParkedPanelJson>>(jsonOptions) ?? [];

            return new WorkbenchLayoutState(layout, known,
                parked.ToDictionary(entry => entry.Key, entry => entry.Value.ToParked()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.LogWarning(ex, "Could not read the workbench layout from {Path}", Path);
            return null;
        }
    }

    /// <summary>
    /// Writes the layout, creating the directory if needed. A failure is logged and swallowed — a
    /// layout that cannot be saved must not take the editor down with it.
    /// </summary>
    public void Save(WorkbenchLayoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var root = new JsonObject
            {
                ["layout"] = JsonNode.Parse(state.Layout.ToJson()),
                ["known"] = JsonSerializer.SerializeToNode(state.KnownPanels.Order().ToList(), jsonOptions),
                ["parked"] = JsonSerializer.SerializeToNode(
                    state.ParkedPanels.ToDictionary(entry => entry.Key, entry => ParkedPanelJson.From(entry.Value)),
                    jsonOptions),
            };

            File.WriteAllText(Path, root.ToJsonString(jsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not save the workbench layout to {Path}", Path);
        }
    }

    /// <summary>The on-disk shape of a <see cref="ParkedPanel"/>; <see cref="Rect"/> has no JSON form of its own.</summary>
    sealed record ParkedPanelJson(DockZone Zone, string? TabbedWith, float[]? FloatBounds)
    {
        public static ParkedPanelJson From(ParkedPanel parked) => new(parked.Zone, parked.TabbedWith,
            parked.FloatBounds is { } b ? [b.X, b.Y, b.W, b.H] : null);

        public ParkedPanel ToParked() => new(Zone, TabbedWith,
            FloatBounds is [var x, var y, var w, var h] ? new Rect(x, y, w, h) : null);
    }
}
