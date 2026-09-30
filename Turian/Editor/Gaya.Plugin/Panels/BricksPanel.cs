using Gaya.Packages;

namespace Gaya.Plugin.Turian;

/// <summary>
/// The Bricks panel: what the project installs, with install, update, remove and the local/global state, and the selected brick's
/// details including why it is installed. It draws <see cref="BricksController"/> and holds no logic of its own.
/// </summary>
/// <param name="controller">The bricks' state and actions.</param>
sealed class BricksPanel(BricksController controller) : IPanel
{
    const float rowHeight = 42f;
    const int maxAssetRows = 40;

    string idInput = "";
    string sourceInput = "";
    bool loaded;
    string? assetsOf;
    IReadOnlyList<string> assets = [];

    static StudioTheme Theme => StudioTheme.Current;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        if (!loaded)
        {
            loaded = true;
            controller.Refresh();
        }

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(Theme.Gap).Padding(10f, 8f).Enter())
        {
            if (!controller.HasProject)
            {
                Line(gui, "Open a project to manage its bricks.", Theme.InkDim);
                return;
            }

            using (gui.Node().Expand().Direction(Axis.Vertical).Gap(2f).Enter())
            {
                Scroll(gui);
                using (gui.Node().ExpandWidth().MinWidth(Theme.Scale(600f)).Direction(Axis.Vertical)
                           .Gap(Theme.Gap).Enter())
                {
                    AddRow(gui);
                    Notice(gui);
                    foreach (var row in controller.Rows) Row(gui, row);
                    if (controller.Rows.Count == 0) Line(gui, "No bricks installed.", Theme.InkDim);
                    Details(gui);
                }
            }
        }
    }

    static void Scroll(Gui gui)
    {
        var node = gui.CurrentNode;
        var previous = gui.GetScrollState(node.Id);
        if (gui.Pass == Pass.Pass1Build && previous?.ShowScrollbarY != true)
            node.PaddingRight(node.Style.PaddingRight + 12f);
        gui.Scroll(Theme.InkDim, Theme.Chrome);
        if (gui.Pass != Pass.Pass2Render || gui.GetScrollState(node.Id)?.ShowScrollbarY == true) return;

        var rect = node.Rect;
        gui.DrawRectFilled(new Rect(rect.X + rect.W - 12f, rect.Y, 12f, rect.H), Theme.Chrome);
        gui.DrawRectFilled(new Rect(rect.X + rect.W - 10f, rect.Y + 2f, 8f, Math.Max(0, rect.H - 4f)),
            Theme.InkDim, 3f);
    }

    void AddRow(Gui gui)
    {
        var height = Theme.Scale(Theme.RowHeight + 4f);

        using (gui.Node(-1, height, "bricks/add").ExpandWidth().Direction(Axis.Horizontal).Gap(Theme.Gap).Enter())
        {
            using (gui.Node(-1, height, "bricks/add/id").Expand().Enter())
                idInput = gui.TextInput(idInput, width: 0, height: height, placeholder: "Brick id, e.g. org.mass4.turian.ui",
                    fontSize: Theme.Text(12), padding: 5, id: "bricks/id");
            using (gui.Node(-1, height, "bricks/add/source").Expand().Enter())
                sourceInput = gui.TextInput(sourceInput, width: 0, height: height,
                    placeholder: "Source: builtin:, file:, git+ (empty for built-in)",
                    fontSize: Theme.Text(12), padding: 5, id: "bricks/source");

            if (Button(gui, "Install", "bricks/install", 70f) && idInput.Trim().Length > 0)
            {
                _ = controller.InstallAsync(idInput, sourceInput);
                idInput = sourceInput = "";
            }

            if (Button(gui, "Restore", "bricks/restore", 70f)) _ = controller.RestoreAsync();
            if (Button(gui, "Update All", "bricks/update", 84f)) _ = controller.UpdateAsync();
        }
    }

    void Notice(Gui gui)
    {
        if (controller.IsBusy) Line(gui, "Working…", Theme.InkDim);
        else if (controller.Error is { } error) Line(gui, error, Theme.Error);
    }

    void Row(Gui gui, BrickRow row)
    {
        var id = $"bricks/row/{row.Id}";
        var height = Theme.Scale(rowHeight);
        var selected = controller.Selected == row.Id;

        using (gui.Node(-1, height, id).ExpandWidth().Direction(Axis.Horizontal).Gap(8f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            if (gui.Pass == Pass.Pass2Render)
            {
                if (selected) gui.DrawBackgroundRect(Theme.Hover, 3f);
                else if (interactable.OnHover()) gui.DrawBackgroundRect(Theme.Chrome, 3f);
                if (interactable.OnClick()) controller.Selected = row.Id;
            }

            using (gui.Node(-1, height, $"{id}/name").Expand().Direction(Axis.Vertical).Gap(2f).Enter())
            {
                gui.ClipContent();
                gui.DrawText(row.DisplayName is { Length: > 0 } name ? name : row.Id, Theme.Text(12f),
                    row.IsDirect ? Theme.Ink : Theme.InkDim, centerInRect: false);
                gui.DrawText(row.Id, Theme.Text(11f), Theme.InkDim, centerInRect: false);
            }

            Cell(gui, $"{id}/version", row.Version, 70f, height);
            Cell(gui, $"{id}/origin", Describe(row.Origin), 110f, height);

            if (row.IsDirect && Button(gui, "Remove", $"{id}/remove", 62f)) _ = controller.RemoveAsync(row.Id);
        }
    }

    void Details(Gui gui)
    {
        if (controller.SelectedBrick is not { } brick) return;

        var manifest = brick.Manifest;
        Line(gui, "", Theme.InkDim);
        Line(gui, $"{manifest.DisplayName ?? brick.Id} {brick.Version}", Theme.Ink);
        if (manifest.Description is { Length: > 0 } description) Line(gui, description, Theme.InkDim);

        Line(gui, $"Source: {brick.Source} ({Describe(brick.Origin)}{(brick.IsOverridden ? ", overridden on this machine" : "")})", Theme.InkDim);
        if (manifest.License is { Length: > 0 } license) Line(gui, $"License: {license}", Theme.InkDim);
        if (manifest.Author is { Length: > 0 } author) Line(gui, $"Author: {author}", Theme.InkDim);
        if (manifest.Categories.Count > 0) Line(gui, $"Categories: {string.Join(", ", manifest.Categories)}", Theme.InkDim);

        var needs = manifest.Dependencies.Keys.Order(StringComparer.Ordinal).ToList();
        Line(gui, needs.Count == 0 ? "Needs: nothing" : $"Needs: {string.Join(", ", needs)}", Theme.InkDim);

        var requiredBy = controller.RequiredBy(brick.Id);
        Line(gui, requiredBy.Count == 0
            ? (brick.Depth == 1 ? "Installed by the project" : "Installed as a dependency")
            : $"Required by: {string.Join(", ", requiredBy)}", Theme.InkDim);
        if (BrickAssemblies.IsPrecast(brick)) Line(gui, "Ships prebuilt assemblies", Theme.InkDim);
        Locality(gui, brick);

        // Copying one asset detaches it from the brick: it gets a new id and stays put when the brick updates.
        if (assetsOf != brick.Id)
        {
            assetsOf = brick.Id;
            assets = BrickAssetCopy.Assets(brick);
        }

        if (assets.Count > 0) Line(gui, "Assets (Copy makes your own version in the project):", Theme.Ink);
        foreach (var asset in assets.Take(maxAssetRows)) AssetRow(gui, brick.Id, asset);
        if (assets.Count > maxAssetRows) Line(gui, $"… and {assets.Count - maxAssetRows} more; turian-cli brick copy copies any of them.", Theme.InkDim);
    }

    void Locality(Gui gui, ResolvedPackage brick)
    {
        var local = brick.Origin == PackageOrigin.Embedded;
        var folder = $"{ProjectManifest.DirectoryName}/{brick.Id}";
        Line(gui, local ? "Local: a copy in this project, committed with it" : "Global: shared by every project on this machine",
            Theme.InkDim);
        Line(gui, $"Installed at: {brick.RootPath}", Theme.InkDim);

        if (local && brick.Manifest.Upstream is not null)
        {
            if (Button(gui, "Revert to global", $"bricks/revert/{brick.Id}", 110f,
                    $"Moves {folder} to .Cache/Trash, with your changes, and uses the shared version again."))
                _ = controller.RevertAsync(brick.Id);
        }
        else if (!local && brick.Origin != PackageOrigin.File)
        {
            if (Button(gui, "Make local", $"bricks/local/{brick.Id}", 90f,
                    $"Copies this brick to {folder} so you can edit it and keep it in version control."))
                _ = controller.EmbedAsync(brick.Id);
        }
    }

    void AssetRow(Gui gui, string brickId, string asset)
    {
        var id = $"bricks/asset/{brickId}/{asset}";
        var height = Theme.Scale(rowHeight);

        using (gui.Node(-1, height, id).ExpandWidth().Direction(Axis.Horizontal).Gap(8f).ContentAlignY(0.5f).Enter())
        {
            using (gui.Node(-1, height, $"{id}/name").Expand().ContentAlignY(0.5f).Enter())
                gui.DrawText(asset, Theme.Text(11f), Theme.InkDim, centerInRect: false);
            if (Button(gui, "Copy", $"{id}/copy", 56f)) _ = controller.CopyAssetsAsync(brickId, [asset]);
        }
    }

    static string Describe(PackageOrigin origin) => origin switch
    {
        PackageOrigin.Builtin => "built-in",
        PackageOrigin.Embedded => "local",
        PackageOrigin.File => "folder",
        PackageOrigin.Git => "git",
        PackageOrigin.Archive => ".brick",
        _ => origin.ToString(),
    };

    static void Cell(Gui gui, string id, string text, float width, float height)
    {
        using (gui.Node(Theme.Scale(width), height, id).ContentAlignY(0.5f).Enter())
            gui.DrawText(text, Theme.Text(11f), Theme.InkDim, centerInRect: false);
    }

    static void Line(Gui gui, string text, Guinevere.Color color)
    {
        using (gui.Node(-1, Theme.Scale(rowHeight - 4f), $"bricks/line/{text}").ExpandWidth().ContentAlignY(0.5f).Enter())
            gui.DrawText(text, Theme.Text(11f), color, centerInRect: false);
    }

    bool Button(Gui gui, string label, string id, float width, string? tooltip = null) =>
        StudioControls.SmallTextButton(gui, label, id, Theme.Scale(width), tooltip) && !controller.IsBusy;
}
