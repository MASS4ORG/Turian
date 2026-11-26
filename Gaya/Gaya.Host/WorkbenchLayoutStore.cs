namespace Gaya.Host;

/// <summary>
/// Where a panel sat when it left the layout without the user closing it — its plugin unloaded, or a
/// user assembly was still compiling at startup — so it can return there once it registers again.
/// </summary>
/// <param name="Zone">The zone of the tab group it was in.</param>
/// <param name="TabbedWith">A panel it shared a tab group with, or null when it was alone.</param>
/// <param name="FloatBounds">The floating window it was in, or null when it was docked.</param>
public sealed record ParkedPanel(DockZone Zone, string? TabbedWith = null, Rect? FloatBounds = null);

/// <summary>What the workbench persists for one layout: the dock tree and what it knows about the panels outside it.</summary>
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

/// <summary>Everything read from the layout file.</summary>
/// <param name="Layouts">The saved layout of each document kind, by kind id, and of no document under its own key.</param>
/// <param name="Legacy">
/// The single layout an earlier version wrote, which seeds every kind that has none of its own; null for a current file.
/// </param>
public sealed record WorkbenchLayouts(
    IReadOnlyDictionary<string, WorkbenchLayoutState> Layouts,
    WorkbenchLayoutState? Legacy = null);

/// <summary>
/// Persists the workbench's <see cref="DockLayout"/>s between runs, one per document kind plus one for no document. The
/// file is a convenience, never a requirement: anything unreadable, from another schema version, or naming panels no
/// plugin contributes is discarded and the layout is re-seeded from the registry.
/// </summary>
public sealed class WorkbenchLayoutStore(ILogger log, string? path = null)
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Width of the left dock column in a seeded layout.</summary>
    public const float DefaultLeftWidth = 300f;

    /// <summary>Width of the right dock column in a seeded layout.</summary>
    public const float DefaultRightWidth = 340f;

    /// <summary>Height of the bottom dock row in a seeded layout.</summary>
    public const float DefaultBottomHeight = 240f;

    /// <summary>The file format version this store writes; a file from a newer version is ignored.</summary>
    public const int FormatVersion = 2;

    /// <summary>The key the layout shown while no document is active is saved under, which no document kind may use.</summary>
    public const string NoDocumentKey = "(none)";

    /// <summary>
    /// Where the layout is written: <c>~/.gaya/layout.json</c> unless the
    /// caller supplies its own path.
    /// </summary>
    public string Path { get; } = path ?? UserConfigPath.For("layout.json");

    /// <summary>
    /// Reads the saved layouts, or returns null when there is nothing usable to read. A file holding one layout, as
    /// earlier versions wrote, comes back as <see cref="WorkbenchLayouts.Legacy"/>; a layout entry that cannot be read is
    /// left out while the others are kept.
    /// </summary>
    public WorkbenchLayouts? Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            var text = File.ReadAllText(Path);
            if (JsonNode.Parse(text) is not JsonObject root)
            {
                return null;
            }

            return ReadRoot(root, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException or FormatException)
        {
            log.LogWarning(ex, "Could not read the workbench layout from {Path}", Path);
            return null;
        }
    }

    /// <summary>Tells the current format from the single-layout formats and reads whichever the file holds.</summary>
    WorkbenchLayouts? ReadRoot(JsonObject root, string text)
    {
        if (root["layouts"] is JsonObject layouts)
        {
            return root["version"]?.GetValue<int>() == FormatVersion ? new WorkbenchLayouts(ReadLayouts(layouts)) : null;
        }

        var legacy = root["layout"] is null
            ? DockLayout.FromJson(text) is { } bare ? new WorkbenchLayoutState(bare, [], []) : null
            : ReadEntry(root);
        return legacy is null ? null : new WorkbenchLayouts(new Dictionary<string, WorkbenchLayoutState>(), legacy);
    }

    /// <summary>
    /// Writes the layouts, creating the directory if needed. A failure is logged and swallowed — a
    /// layout that cannot be saved must not take the editor down with it.
    /// </summary>
    /// <param name="layouts">The layout state of each kind by kind id, and of no document under <see cref="NoDocumentKey"/>.</param>
    public void Save(IReadOnlyDictionary<string, WorkbenchLayoutState> layouts)
    {
        ArgumentNullException.ThrowIfNull(layouts);

        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var entries = new JsonObject();
            foreach (var (key, state) in layouts.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                entries[key] = new JsonObject
                {
                    ["layout"] = JsonNode.Parse(state.Layout.ToJson()),
                    ["known"] = JsonSerializer.SerializeToNode(state.KnownPanels.Order().ToList(), JsonOptions),
                    ["parked"] = JsonSerializer.SerializeToNode(
                        state.ParkedPanels.ToDictionary(entry => entry.Key, entry => ParkedPanelJson.From(entry.Value)),
                        JsonOptions),
                };
            }

            var root = new JsonObject { ["version"] = FormatVersion, ["layouts"] = entries };
            File.WriteAllText(Path, root.ToJsonString(JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not save the workbench layout to {Path}", Path);
        }
    }

    Dictionary<string, WorkbenchLayoutState> ReadLayouts(JsonObject layouts)
    {
        var result = new Dictionary<string, WorkbenchLayoutState>();
        foreach (var (key, node) in layouts)
        {
            try
            {
                if (node is JsonObject entry && ReadEntry(entry) is { } state)
                {
                    result[key] = state;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                log.LogWarning(ex, "Ignored the unreadable workbench layout {Layout} in {Path}", key, Path);
            }
        }

        return result;
    }

    /// <summary>Reads one layout with its known and parked panels, or null when the dock tree is unusable.</summary>
    static WorkbenchLayoutState? ReadEntry(JsonObject entry)
    {
        if (entry["layout"] is not { } layoutNode
            || DockLayout.FromJson(layoutNode.ToJsonString()) is not { } layout)
        {
            return null;
        }

        var known = Read<HashSet<string>>(entry["known"]);
        known.UnionWith(layout.PanelIds);
        var parked = Read<Dictionary<string, ParkedPanelJson>>(entry["parked"]);

        return new WorkbenchLayoutState(layout, known,
            parked.ToDictionary(static item => item.Key, static item => item.Value.ToParked()));
    }

    static T Read<T>(JsonNode? node) where T : new() => node is null ? new T() : node.Deserialize<T>(JsonOptions) ?? new T();

    /// <summary>The on-disk shape of a <see cref="ParkedPanel"/>; <see cref="Rect"/> has no JSON form of its own.</summary>
    sealed record ParkedPanelJson(DockZone Zone, string? TabbedWith, float[]? FloatBounds)
    {
        public static ParkedPanelJson From(ParkedPanel parked) => new(parked.Zone, parked.TabbedWith,
            parked.FloatBounds is { } b ? [b.X, b.Y, b.W, b.H] : null);

        public ParkedPanel ToParked() => new(Zone, TabbedWith,
            FloatBounds is [var x, var y, var w, var h] ? new Rect(x, y, w, h) : null);
    }
}
