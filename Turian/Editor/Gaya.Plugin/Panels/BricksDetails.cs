using Gaya.Packages;

namespace Gaya.Plugin.Turian;

/// <summary>
/// The Bricks panel's right-hand side: what the selected brick is, where it came from and where it is, and the
/// actions that move it between available, installed and in use. Everything comes from
/// <see cref="BricksController"/>.
/// </summary>
/// <param name="controller">The bricks' state and actions.</param>
sealed class BricksDetails(BricksController controller)
{
    const float rowHeight = 32f;
    const int maxAssetRows = 40;

    string? assetsOf;
    IReadOnlyList<string> assets = [];

    static StudioTheme Theme => StudioTheme.Current;

    /// <summary>Draws the selected brick, or a hint when none is selected.</summary>
    /// <param name="gui">The GUI for this frame.</param>
    public void Render(Gui gui)
    {
        if (controller.Catalog.FirstOrDefault(b => b.Id == controller.Selected) is not { } brick)
        {
            Line(gui, "Select a brick to see its details.", Theme.InkDim);
            return;
        }

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(2f).Enter())
        {
            gui.ScrollY();
            Header(gui, brick);
            Actions(gui, brick);

            if (brick.Description is { Length: > 0 } description) Line(gui, description, Theme.Ink);
            Line(gui, "", Theme.InkDim);
            Line(gui, $"Name: {brick.Id}", Theme.InkDim);
            Line(gui, $"Origin: {brick.Origin ?? "unknown"}", Theme.InkDim);

            if (controller.SelectedBrick is { } installed) Installed(gui, installed);
        }
    }

    /// <summary>Draws one line of text in a row that clips instead of widening the panel.</summary>
    /// <param name="gui">The GUI for this frame.</param>
    /// <param name="text">The text.</param>
    /// <param name="color">The text color.</param>
    public static void Line(Gui gui, string text, Guinevere.Color color)
    {
        using (gui.Node(-1, Theme.Scale(rowHeight - 6f), $"bricks/line/{text}").ExpandWidth().ContentAlignY(0.5f).Enter())
        {
            gui.ClipContent();
            gui.DrawText(text, Theme.Text(11f), color, centerInRect: false);
        }
    }

    /// <summary>Draws a fixed-width cell of dim text.</summary>
    /// <param name="gui">The GUI for this frame.</param>
    /// <param name="id">The cell's node id.</param>
    /// <param name="text">The text.</param>
    /// <param name="width">The cell width in unscaled pixels.</param>
    /// <param name="height">The cell height in scaled pixels.</param>
    public static void Cell(Gui gui, string id, string text, float width, float height)
    {
        using (gui.Node(Theme.Scale(width), height, id).ContentAlignY(0.5f).Enter())
        {
            gui.ClipContent();
            gui.DrawText(text, Theme.Text(11f), Theme.InkDim, centerInRect: false);
        }
    }

    static void Header(Gui gui, CatalogBrick brick)
    {
        var state = brick.State switch
        {
            BrickState.Enabled => "in use",
            BrickState.Installed => "on this machine, not in use",
            _ => "available",
        };
        var version = brick.InstalledVersion ?? brick.LatestVersion;

        using (gui.Node(-1, Theme.Scale(24f), "bricks/details/title").ExpandWidth().ContentAlignY(0.5f).Enter())
        {
            gui.ClipContent();
            gui.DrawText(brick.DisplayName is { Length: > 0 } name ? name : brick.Id, Theme.Text(15f), Theme.Ink,
                centerInRect: false);
        }

        Line(gui, $"{version} · {state}", Theme.InkDim);
        if (brick.Author is { Length: > 0 } author) Line(gui, $"by {author}", Theme.InkDim);
    }

    void Actions(Gui gui, CatalogBrick brick)
    {
        var height = Theme.Scale(Theme.RowHeight + 4f);
        var resolved = controller.SelectedBrick;
        var local = resolved?.Origin == PackageOrigin.Embedded;

        using (gui.Node(-1, height, "bricks/details/actions").ExpandWidth().Direction(Axis.Horizontal).Gap(Theme.Gap)
                   .ContentAlignY(0.5f).Enter())
        {
            if (brick.State == BrickState.Enabled && !local
                && Button(gui, "Disable", "bricks/details/disable", 66f,
                    "Stops using this brick in the project. It stays on this machine."))
                _ = controller.DisableAsync(brick.Id);

            if (brick.State != BrickState.Enabled
                && Button(gui, "Enable", "bricks/details/enable", 60f,
                    brick.State == BrickState.Available
                        ? "Downloads this brick if needed and uses it in the project."
                        : "Uses this brick in the project."))
                _ = controller.EnableAsync(brick);

            if (brick.HasUpdate && brick.State == BrickState.Enabled
                && Button(gui, "Update", "bricks/details/update", 60f, $"Moves to {brick.LatestVersion}."))
                _ = controller.UpdateAsync(brick.Id);

            if (brick.IsInstalled && Button(gui, "Uninstall", "bricks/details/uninstall", 70f,
                    local ? "Deletes this project's copy; it moves to the project's trash."
                        : "Deletes the downloaded brick from this machine. Every project loses it."))
                _ = controller.UninstallAsync(brick.Id);

            if (resolved is { } brickFolder) Locality(gui, brickFolder);
        }
    }

    void Locality(Gui gui, ResolvedPackage brick)
    {
        var folder = $"{ProjectManifest.DirectoryName}/{brick.Id}";

        if (brick.Origin == PackageOrigin.Embedded && brick.Manifest.Upstream is not null)
        {
            if (Button(gui, "Revert to global", $"bricks/revert/{brick.Id}", 110f,
                    $"Moves {folder} to .Cache/Trash, with your changes, and uses the shared version again."))
                _ = controller.RevertAsync(brick.Id);
        }
        else if (brick.Origin is not (PackageOrigin.Embedded or PackageOrigin.File))
        {
            if (Button(gui, "Make local", $"bricks/local/{brick.Id}", 90f,
                    $"Copies this brick to {folder} so you can edit it and keep it in version control."))
                _ = controller.EmbedAsync(brick.Id);
        }
    }

    void Installed(Gui gui, ResolvedPackage brick)
    {
        var manifest = brick.Manifest;
        var where = brick.Origin == PackageOrigin.Embedded ? "local to this project" : "global, shared by every project on this machine";
        Line(gui, $"Installed at: {brick.RootPath} ({where})", Theme.InkDim);
        if (manifest.License is { Length: > 0 } license) Line(gui, $"License: {license}", Theme.InkDim);
        if (manifest.Categories.Count > 0) Line(gui, $"Categories: {string.Join(", ", manifest.Categories)}", Theme.InkDim);

        var needs = manifest.Dependencies.Keys.Order(StringComparer.Ordinal).ToList();
        Line(gui, needs.Count == 0 ? "Needs: nothing" : $"Needs: {string.Join(", ", needs)}", Theme.InkDim);

        var requiredBy = controller.RequiredBy(brick.Id);
        Line(gui, requiredBy.Count == 0
            ? (brick.Depth == 1 ? "Used by the project" : "Used as a dependency")
            : $"Needed by: {string.Join(", ", requiredBy)}", Theme.InkDim);
        if (brick.IsOverridden) Line(gui, "Source overridden on this machine", Theme.InkDim);
        if (BrickAssemblies.IsPrecast(brick)) Line(gui, "Ships prebuilt assemblies", Theme.InkDim);

        // Copying one asset detaches it from the brick: it gets a new id and stays put when the brick updates.
        if (assetsOf != brick.Id)
        {
            assetsOf = brick.Id;
            assets = BrickAssetCopy.Assets(brick);
        }

        if (assets.Count > 0) Line(gui, "Assets (Copy makes your own version in the project):", Theme.Ink);
        foreach (var asset in assets.Take(maxAssetRows)) AssetRow(gui, brick.Id, asset);
        if (assets.Count > maxAssetRows)
            Line(gui, $"… and {assets.Count - maxAssetRows} more; turian-cli brick copy copies any of them.", Theme.InkDim);
    }

    void AssetRow(Gui gui, string brickId, string asset)
    {
        var id = $"bricks/asset/{brickId}/{asset}";
        var height = Theme.Scale(rowHeight);

        using (gui.Node(-1, height, id).ExpandWidth().Direction(Axis.Horizontal).Gap(8f).ContentAlignY(0.5f).Enter())
        {
            using (gui.Node(-1, height, $"{id}/name").Expand().ContentAlignY(0.5f).Enter())
            {
                gui.ClipContent();
                gui.DrawText(asset, Theme.Text(11f), Theme.InkDim, centerInRect: false);
            }

            if (Button(gui, "Copy", $"{id}/copy", 56f)) _ = controller.CopyAssetsAsync(brickId, [asset]);
        }
    }

    bool Button(Gui gui, string label, string id, float width, string? tooltip = null) =>
        StudioControls.SmallTextButton(gui, label, id, Theme.Scale(width), tooltip) && !controller.IsBusy;
}
