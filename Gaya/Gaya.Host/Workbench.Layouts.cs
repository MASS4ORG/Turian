namespace Gaya.Host;

/// <summary>
/// The dock layouts: one per document kind plus one for no document, each remembering which panels it has seen, which
/// the user closed and which wait for their plugin to register again.
/// </summary>
public sealed partial class Workbench
{
    /// <summary>One layout with the bookkeeping that decides what it holds.</summary>
    sealed class LayoutSlot(string? kind, DockLayout layout)
    {
        /// <summary>The document kind the layout belongs to, or null for the no-document layout.</summary>
        public string? Kind { get; } = kind;

        public DockLayout Layout { get; } = layout;

        /// <summary>Panels the layout has accounted for; a known panel outside the tree was closed.</summary>
        public HashSet<string> Known { get; } = [];

        /// <summary>Panels waiting to return where they were.</summary>
        public Dictionary<string, ParkedPanel> Parked { get; } = [];

        public int SavedRevision { get; set; }

        public string Key => Kind ?? WorkbenchLayoutStore.NoDocumentKey;
    }

    readonly Dictionary<string, LayoutSlot> slots = [];
    WorkbenchLayouts? savedLayouts;
    LayoutSlot activeSlot;

    /// <summary>The dock arrangement of the active document's kind, or of no document, currently on screen.</summary>
    public DockLayout Layout => activeSlot.Layout;

    /// <summary>The layout of a kind, restored or seeded the first time it is needed.</summary>
    LayoutSlot SlotFor(string? kind)
    {
        var key = kind ?? WorkbenchLayoutStore.NoDocumentKey;
        if (slots.TryGetValue(key, out var slot))
        {
            return slot;
        }

        slot = RestoreOrSeed(kind);
        slot.SavedRevision = slot.Layout.Revision;
        slots[key] = slot;
        return slot;
    }

    /// <summary>
    /// Loads a kind's saved layout and reconciles it against the panels actually registered — panels not registered
    /// yet are parked until they are, never-seen ones that open by default are folded in at their declared placement,
    /// panels that kind may not hold are dropped — or seeds a fresh layout when nothing usable was saved. A kind with
    /// no layout of its own starts from the single layout an earlier version saved.
    /// </summary>
    LayoutSlot RestoreOrSeed(string? kind)
    {
        if (SavedStateFor(kind) is not { } state)
        {
            return Seed(kind);
        }

        var slot = new LayoutSlot(kind, state.Layout);
        slot.Known.UnionWith(state.KnownPanels);
        foreach (var (panelId, parked) in state.ParkedPanels)
        {
            slot.Parked[panelId] = parked;
        }

        // A layout that knows no panels predates closed panels being remembered: it holds every panel
        // that was ever registered, wanted or not, so only those that open by default are kept.
        AdmitRestored(slot, obsolete: state.KnownPanels.Count == 0);
        foreach (var descriptor in app.Panels.All.Where(d => d.AllowedIn(kind)))
        {
            Reconcile(slot, descriptor);
        }

        log.LogDebug("Restored workbench layout {Layout} from {Path}", slot.Key, layoutStore.Path);
        return slot;
    }

    /// <summary>What was saved for a kind, else a copy of the legacy layout, else nothing.</summary>
    WorkbenchLayoutState? SavedStateFor(string? kind)
    {
        if (savedLayouts is null)
        {
            return null;
        }

        if (savedLayouts.Layouts.TryGetValue(kind ?? WorkbenchLayoutStore.NoDocumentKey, out var stored))
        {
            return stored;
        }

        return savedLayouts.Legacy is { } legacy ? Copy(legacy) : null;
    }

    /// <summary>Drops restored panels the layout may not hold and parks those no plugin registers yet.</summary>
    void AdmitRestored(LayoutSlot slot, bool obsolete)
    {
        foreach (var panelId in slot.Layout.PanelIds.ToList())
        {
            if (!descriptors.TryGetValue(panelId, out var descriptor))
            {
                if (obsolete)
                {
                    slot.Layout.Remove(panelId);
                }
                else
                {
                    Park(slot, panelId);
                }
            }
            else if (!descriptor.AllowedIn(slot.Kind) || (obsolete && !descriptor.OpenByDefault))
            {
                slot.Layout.Remove(panelId);
            }
        }
    }

    /// <summary>An independent copy of a saved layout, so one legacy layout can seed several kinds.</summary>
    static WorkbenchLayoutState Copy(WorkbenchLayoutState state) =>
        new(DockLayout.FromJson(state.Layout.ToJson()) ?? new DockLayout(), [.. state.KnownPanels],
            new Dictionary<string, ParkedPanel>(state.ParkedPanels));

    /// <summary>
    /// Brings a registered panel into the layout if it belongs there: back to where it was parked, or at
    /// its declared placement the first time it is seen. A known panel that is absent was closed.
    /// </summary>
    void Reconcile(LayoutSlot slot, PanelDescriptor descriptor)
    {
        if (slot.Layout.Contains(descriptor.Id))
        {
            slot.Known.Add(descriptor.Id);
            return;
        }

        if (slot.Parked.ContainsKey(descriptor.Id) || (slot.Known.Add(descriptor.Id) && descriptor.OpenByDefault))
        {
            Place(slot, descriptor);
        }
    }

    /// <summary>
    /// Adds a panel where it was parked, falling back to its declared placement. Rejoining a tab group
    /// keeps that group's active tab. A document-bound panel is never added to a layout of a kind it is not bound to.
    /// </summary>
    void Place(LayoutSlot slot, PanelDescriptor descriptor)
    {
        if (!descriptor.AllowedIn(slot.Kind))
        {
            return;
        }

        var layout = slot.Layout;
        var panelId = descriptor.Id;
        var placement = PlacementFor(slot.Kind, descriptor);
        slot.Known.Add(panelId);

        if (slot.Parked.Remove(panelId, out var parked))
        {
            if (parked.TabbedWith is { } sibling && layout.FindLeaf(sibling) is { } leaf)
            {
                var active = leaf.PanelIds.ElementAtOrDefault(leaf.ActiveIndex);
                layout.DockInto(panelId, leaf, DockZone.Center);
                if (active is not null)
                {
                    layout.Activate(active);
                }

                return;
            }

            if (parked.FloatBounds is { } bounds)
            {
                layout.Float(panelId, bounds);
                return;
            }

            layout.EnsurePanel(panelId, parked.Zone, FractionFor(placement));
            return;
        }

        if (placement == PanelPlacement.Floating)
        {
            layout.Float(panelId, FloatingBounds);
        }
        else
        {
            layout.EnsurePanel(panelId, ZoneFor(placement), FractionFor(placement));
        }
    }

    static readonly Rect FloatingBounds = new(180, 140, WorkbenchLayoutStore.DefaultLeftWidth,
        WorkbenchLayoutStore.DefaultBottomHeight);

    /// <summary>Where a kind's layout docks a panel by default: the kind's own choice, else the panel's.</summary>
    PanelPlacement PlacementFor(string? kind, PanelDescriptor descriptor) =>
        kind is not null && app.DocumentKinds.Find(kind)?.Placements.TryGetValue(descriptor.Id, out var placement) == true
            ? placement
            : descriptor.DefaultPlacement;

    /// <summary>Records where a panel sits and takes it out of the layout until it registers again.</summary>
    void Park(LayoutSlot slot, string panelId)
    {
        if (Snapshot(slot.Layout, panelId) is not { } parked)
        {
            return;
        }

        slot.Parked[panelId] = parked;
        slot.Layout.Remove(panelId);
    }

    /// <summary>
    /// Reconciles the panel set against whatever plugins currently register — a user assembly's own
    /// panels come and go with a recompile, the same way its commands and menu items already do. Cheap
    /// when nothing changed: a revision compare against <see cref="PanelRegistry"/>.
    /// </summary>
    void SyncPanels()
    {
        if (app.Panels.Revision == panelsRevision)
        {
            return;
        }

        panelsRevision = app.Panels.Revision;
        var current = app.Panels.All;
        ReleaseReplacedPanels(current);
        ParkGonePanels(current);
        descriptors = current.ToDictionary(static descriptor => descriptor.Id);

        foreach (var slot in slots.Values)
        {
            foreach (var descriptor in current.Where(d => d.AllowedIn(slot.Kind)))
            {
                Reconcile(slot, descriptor);
            }
        }
    }

    /// <summary>Releases panels that are no longer registered and parks them; a recompile registers them again moments later.</summary>
    void ParkGonePanels(IReadOnlyList<PanelDescriptor> current)
    {
        foreach (var goneId in descriptors.Keys.Except(current.Select(static d => d.Id)).ToList())
        {
            ReleasePanel(goneId);
            foreach (var slot in slots.Values)
            {
                Park(slot, goneId);
            }
        }
    }

    void ReleaseReplacedPanels(IReadOnlyList<PanelDescriptor> current)
    {
        foreach (var descriptor in current)
        {
            if (descriptors.TryGetValue(descriptor.Id, out var previous) && !ReferenceEquals(previous, descriptor))
            {
                ReleasePanel(descriptor.Id);
            }
        }
    }

    /// <summary>Builds a kind's initial arrangement from the declared placement of each allowed panel that opens by default.</summary>
    LayoutSlot Seed(string? kind)
    {
        var slot = new LayoutSlot(kind, new DockLayout());
        var layout = slot.Layout;
        var allowed = app.Panels.All.Where(d => d.AllowedIn(kind)).ToList();
        slot.Known.UnionWith(allowed.Select(static descriptor => descriptor.Id));
        var opening = allowed.Where(static descriptor => descriptor.OpenByDefault)
            .Select(descriptor => (Descriptor: descriptor, Placement: PlacementFor(kind, descriptor))).ToList();

        // Center first: the edges wrap whatever is already there, so seeding an edge into an empty
        // layout would make that panel the center.
        foreach (var (descriptor, _) in opening.Where(static entry => entry.Placement == PanelPlacement.Center))
        {
            layout.DockAtEdge(descriptor.Id, DockZone.Center);
        }

        foreach (var (descriptor, placement) in opening.Where(static entry => entry.Placement != PanelPlacement.Center))
        {
            if (placement == PanelPlacement.Floating)
            {
                layout.Float(descriptor.Id, FloatingBounds);
                continue;
            }

            layout.DockAtEdge(descriptor.Id, ZoneFor(placement), FractionFor(placement));
        }

        return slot;
    }

    static DockZone ZoneFor(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left => DockZone.Left,
        PanelPlacement.Right => DockZone.Right,
        PanelPlacement.Bottom => DockZone.Bottom,
        _ => DockZone.Center
    };

    static float FractionFor(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left => WorkbenchLayoutStore.DefaultLeftWidth / 1600f,
        PanelPlacement.Right => WorkbenchLayoutStore.DefaultRightWidth / 1600f,
        PanelPlacement.Bottom => WorkbenchLayoutStore.DefaultBottomHeight / 950f,
        _ => 0.25f
    };

    /// <summary>Contributes the layout reset commands and their View menu entries; they act on this workbench.</summary>
    void RegisterLayoutCommands()
    {
        app.Commands.Register(new CommandDescriptor(ShellCommands.ResetLayout, "View: Reset Layout",
            _ => ResetLayout())
        { MenuLabel = "Reset Layout" });
        app.Commands.Register(new CommandDescriptor(ShellCommands.ResetAllLayouts, "View: Reset All Layouts",
            _ => ResetAllLayouts())
        { MenuLabel = "Reset All Layouts" });
        app.Menus.Add(new MenuItemDescriptor(MenuIds.View, ShellCommands.ResetLayout, "zz-layout", 0));
        app.Menus.Add(new MenuItemDescriptor(MenuIds.View, ShellCommands.ResetAllLayouts, "zz-layout", 1));
    }

    /// <summary>Restores the active layout to the arrangement a fresh install would show.</summary>
    public void ResetLayout() => Reset(activeSlot);

    /// <summary>Restores every layout to the arrangement a fresh install would show.</summary>
    public void ResetAllLayouts()
    {
        savedLayouts = null;
        foreach (var slot in slots.Values)
        {
            Reset(slot);
        }
    }

    /// <summary>Rebuilds a layout in place, so whoever holds it sees the new arrangement.</summary>
    void Reset(LayoutSlot slot)
    {
        var fresh = Seed(slot.Kind);
        slot.Layout.Root = fresh.Layout.Root;
        slot.Layout.Floating.Clear();
        slot.Layout.Floating.AddRange(fresh.Layout.Floating);
        slot.Layout.MarkChanged();
        slot.Known.Clear();
        slot.Known.UnionWith(fresh.Known);
        slot.Parked.Clear();
    }

    /// <summary>Writes every layout out if the user has changed one since they were last saved.</summary>
    public void SaveLayoutIfChanged()
    {
        if (slots.Values.All(static slot => slot.Layout.Revision == slot.SavedRevision))
        {
            return;
        }

        var states = new Dictionary<string, WorkbenchLayoutState>();
        if (savedLayouts is not null)
        {
            foreach (var (key, state) in savedLayouts.Layouts)
            {
                states[key] = state;
            }
        }

        foreach (var (key, slot) in slots)
        {
            slot.SavedRevision = slot.Layout.Revision;
            states[key] = new WorkbenchLayoutState(slot.Layout, slot.Known, slot.Parked);
        }

        layoutStore.Save(states);
    }
}
