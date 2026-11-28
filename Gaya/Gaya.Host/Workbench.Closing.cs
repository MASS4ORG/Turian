namespace Gaya.Host;

/// <summary>Closing panels that implement <see cref="IPanelCloseGuard"/>: they decide before they leave the layout.</summary>
public sealed partial class Workbench
{
    readonly HashSet<InstanceKey> closeRequests = [];

    /// <summary>
    /// Closes an open panel in the active layout. A panel with a close guard is asked first and stays open until it
    /// agrees, which may be after a dialog; the View menu closes panels through here.
    /// </summary>
    /// <param name="panelId">The panel to close.</param>
    public void ClosePanel(string panelId)
    {
        var slot = activeSlot;
        if (!slot.Layout.Contains(panelId))
        {
            return;
        }

        if (descriptors.TryGetValue(panelId, out var descriptor)
            && ExistingInstance(panelId, app.Documents.Active) is IPanelCloseGuard guard)
        {
            Ask(slot, KeyFor(descriptor, app.Documents.Active), guard);
            return;
        }

        slot.Layout.Remove(panelId);
    }

    void Ask(LayoutSlot slot, InstanceKey key, IPanelCloseGuard guard)
    {
        if (!closeRequests.Add(key))
        {
            return;
        }

        var answered = false;
        try
        {
            guard.RequestClose(allowed =>
            {
                if (answered)
                {
                    return;
                }

                answered = true;
                closeRequests.Remove(key);
                if (allowed && slot.Layout.Contains(key.PanelId))
                {
                    slot.Layout.Remove(key.PanelId);
                }
            });
        }
        catch (Exception ex)
        {
            closeRequests.Remove(key);
            log.LogError(ex, "Panel {PanelId} threw while deciding whether to close", key.PanelId);
        }
    }

    /// <summary>Where each guarded panel sits just before the dock draws, which is what a close click removes.</summary>
    Dictionary<string, ParkedPanel>? SnapshotGuarded(Gui gui)
    {
        if (gui.Pass != Pass.Pass2Render)
        {
            return null;
        }

        Dictionary<string, ParkedPanel>? guarded = null;
        foreach (var (key, panel) in panelInstances)
        {
            if (panel is IPanelCloseGuard && descriptors.TryGetValue(key.PanelId, out var descriptor)
                && key == KeyFor(descriptor, frameDocument)
                && Snapshot(frameSlot.Layout, key.PanelId) is { } placement)
            {
                (guarded ??= [])[key.PanelId] = placement;
            }
        }

        return guarded;
    }

    /// <summary>
    /// The dock's own close button removes a panel without asking, so a guarded panel it removed is put back where
    /// it was and the guard decides whether it leaves.
    /// </summary>
    void ReviewClosed(Gui gui, Dictionary<string, ParkedPanel>? guarded)
    {
        if (guarded is null || gui.Pass != Pass.Pass2Render)
        {
            return;
        }

        var slot = frameSlot;
        foreach (var (panelId, placement) in guarded)
        {
            if (slot.Layout.Contains(panelId) || !descriptors.TryGetValue(panelId, out var descriptor)
                || ExistingInstance(panelId, frameDocument) is not IPanelCloseGuard guard)
            {
                continue;
            }

            slot.Parked[panelId] = placement;
            Place(slot, descriptor);
            slot.Layout.Activate(panelId);
            Ask(slot, KeyFor(descriptor, frameDocument), guard);
        }
    }

    /// <summary>Where a panel sits in a layout, in the form a parked panel is restored from; null when it is not there.</summary>
    static ParkedPanel? Snapshot(DockLayout layout, string panelId)
    {
        if (layout.FindLeaf(panelId) is not { } leaf)
        {
            return null;
        }

        var window = layout.Floating.FirstOrDefault(candidate => candidate.Root.Leaves().Contains(leaf));
        return new ParkedPanel(leaf.Zone, leaf.PanelIds.FirstOrDefault(id => id != panelId), window?.Bounds);
    }
}
