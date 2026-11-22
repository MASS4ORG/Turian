namespace Gaya.Packages.Editor;

public sealed partial class BricksPanel
{
    (BrickFilter, string, BrickCategoryFilter) listedView;
    IReadOnlyList<StoredCopy>? cleanup;
    long cleanupBytes;

    void List(Gui gui, Frame now)
    {
        var shown = BrickCatalog.Filter(now.Catalog, filter, Search, Category);
        using (gui.Node(id: "bricks/list").Expand().Direction(Axis.Vertical).Gap(2f).Enter())
        {
            // A shorter list keeps the old scroll offset, which would draw the matches above the visible area.
            var view = (filter, Search.Trim(), Category);
            if (view != listedView)
            {
                listedView = view;
                if (gui.GetScrollState(gui.CurrentNode.Id) is { } scroll)
                {
                    scroll.ScrollOffset = Vector2.Zero;
                }
            }

            gui.ScrollY();
            Group(gui, now, "In use", shown.Where(static b => b.State == BrickState.Enabled));
            Group(gui, now, "Available", shown.Where(static b => b.State != BrickState.Enabled));
            if (shown.Count == 0)
            {
                Line(gui, "No bricks match.", Theme.InkDim);
            }
        }
    }

    /// <summary>The staged changes and the buttons that apply or drop them all, while any wait.</summary>
    void PendingBar(Gui gui, Frame now)
    {
        if (now.Pending.Count == 0)
        {
            return;
        }

        using (gui.Node(-1, Theme.Scale(Theme.RowHeight + 4f), "bricks/pending").ExpandWidth().Direction(Axis.Horizontal)
                   .Gap(Theme.Gap).AlignContentY(0.5f).Enter())
        {
            using (gui.Node(-1, Theme.Scale(Theme.RowHeight), "bricks/pending/text").Expand().AlignContentY(0.5f).Enter())
            {
                gui.ClipContent();
                gui.DrawText(Summary(now.Pending), Theme.Text(11f), Theme.Warning, centerInRect: false);
                gui.Tooltip(gui.CurrentNode, string.Join('\n', now.Pending.Select(static c => c.Description)), maxWidth: 360);
            }

            if (Button(gui, now, "Apply", "bricks/pending/apply", 56f,
                    "Apply every waiting change at once: the bricks resolve and the project recompiles once.", accent: true))
            {
                _ = controller.ApplyPendingAsync();
            }

            if (Button(gui, now, "Discard", "bricks/pending/discard", 60f))
            {
                controller.DiscardPending();
            }
        }
    }

    /// <summary>What the pending bar says: the one change chosen with the bricks that must follow it, else a count.</summary>
    /// <param name="pending">The staged changes.</param>
    /// <returns>The text.</returns>
    public static string Summary(IReadOnlyList<BrickChange> pending)
    {
        var chosen = pending.Where(static c => c.Because is null).ToList();
        if (chosen.Count != 1)
        {
            return $"{pending.Count} changes waiting";
        }

        var followers = pending.Where(static c => c.Because is not null).Select(static c => c.Id).ToList();
        return followers.Count == 0 ? chosen[0].Description : $"{chosen[0].Description}; also disables {string.Join(", ", followers)}";
    }

    void OpenCleanup()
    {
        cleanup = controller.StaleDownloads();
        cleanupBytes = cleanup.Sum(BricksController.SizeOf);
    }

    /// <summary>Asks before deleting old downloads from the shared store, saying how many and how much space.</summary>
    void CleanupRow(Gui gui, Frame now)
    {
        if (cleanup is not { } copies)
        {
            return;
        }

        using (gui.Node(-1, Theme.Scale(Theme.RowHeight + 4f), "bricks/cleanup").ExpandWidth().Direction(Axis.Horizontal)
                   .Gap(Theme.Gap).AlignContentY(0.5f).Enter())
        {
            using (gui.Node(-1, Theme.Scale(Theme.RowHeight), "bricks/cleanup/text").Expand().AlignContentY(0.5f).Enter())
            {
                gui.ClipContent();
                gui.DrawText(copies.Count == 0
                    ? "No old downloads to remove."
                    : $"Remove {copies.Count} old download{(copies.Count == 1 ? "" : "s")} ({BricksController.FormatSize(cleanupBytes)})?",
                    Theme.Text(11f), Theme.Ink, centerInRect: false);
                gui.Tooltip(gui.CurrentNode,
                    "Keeps the newest version of each brick and every version this project or Studio uses. Another project that pinned a removed version downloads it again.",
                    maxWidth: 360);
            }

            if (copies.Count > 0 && Button(gui, now, "Remove", "bricks/cleanup/remove", 64f))
            {
                _ = controller.CleanUpDownloadsAsync();
                cleanup = null;
            }

            if (Button(gui, now, copies.Count == 0 ? "Close" : "Cancel", "bricks/cleanup/cancel", 60f))
            {
                cleanup = null;
            }
        }
    }

    void Group(Gui gui, Frame now, string title, IEnumerable<CatalogBrick> bricks)
    {
        var rows = bricks.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        Line(gui, $"{title} ({rows.Count})", Theme.Ink);
        foreach (var brick in rows)
        {
            Row(gui, now, brick);
        }
    }

    void Row(Gui gui, Frame now, CatalogBrick brick)
    {
        var id = $"bricks/row/{brick.Id}";
        var height = Theme.Scale(rowHeight);
        var pending = now.Pending.LastOrDefault(c => c.Id == brick.Id);

        using (gui.Node(-1, height, id).ExpandWidth().Direction(Axis.Horizontal).Gap(8f).Padding(6f, 0f)
                   .AlignContentY(0.5f).Enter())
        {
            Selectable(gui, controller.Selected == brick.Id, () => controller.Selected = brick.Id);

            var enabled = brick.State == BrickState.Enabled;
            var shown = pending?.Kind switch
            {
                BrickChangeKind.Enable => true,
                BrickChangeKind.Disable => false,
                _ => enabled,
            };
            using (gui.Node(Theme.Scale(18f), height, $"{id}/enabled").AlignContentY(0.5f).Enter())
            {
                if (gui.Checkbox(ref shown, enabled: !now.Busy).Changed)
                {
                    controller.Toggle(brick);
                }
            }

            using (gui.Node(-1, height, $"{id}/name").Expand().Direction(Axis.Vertical).Gap(2f).AlignContentY(0.5f).Enter())
            {
                gui.ClipContent();
                gui.DrawText(brick.DisplayName is { Length: > 0 } name ? name : brick.Id, Theme.Text(12f),
                    enabled ? Theme.Ink : Theme.InkDim, centerInRect: false);
                gui.DrawText(brick.Id, Theme.Text(10f), Theme.InkDim, centerInRect: false);
            }

            if (now.WorkspaceScope is { } scope && !brick.Accepts(scope))
            {
                Cell(gui, $"{id}/scope", brick.Accepts(PackageScope.Studio) ? "Studio" : "Project", 48f, height);
            }

            var (text, color) = VersionCell(brick, pending);
            Cell(gui, $"{id}/version", text, brick.HasUpdate || pending is not null ? 120f : 60f, height, color);
        }
    }

    static (string Text, Color Color) VersionCell(CatalogBrick brick, BrickChange? pending)
    {
        var version = brick.InstalledVersion ?? brick.LatestVersion ?? "";
        return pending is not null
            ? (pending.Transition, Theme.Warning)
            : (brick.HasUpdate ? $"{version} → {brick.LatestVersion}" : version, Theme.InkDim);
    }
}
