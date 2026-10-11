namespace Gaya.Plugin.Turian;

sealed partial class AssetBrowserPanel
{
    const string gridId = "assets/grid";
    IReadOnlyList<AssetEntry> gridEntries = [];
    IReadOnlyList<string> gridIds = [];
    AssetGridWindow gridWindow;
    float gridWidth = 500;
    float gridHeight = 400;
    float gridScroll;
    float cellWidth;
    float cellHeight;
    float previewSize;
    bool gridReset;
    bool gridReveal;

    void Grid(Gui gui)
    {
        if (gui.Pass == Pass.Pass1Build) PrepareGrid();
        using (gui.Node(-1, -1, gridId).Expand().Direction(Axis.Vertical).AlignContent(0, 0).Enter())
        {
            GridScroll(gui);
            if (navigation.Current is { } current && byPath.GetValueOrDefault(current) is { } folder)
                GridDropTarget(gui, folder, "assets/grid/drop");
            var clicked = GridRows(gui);
            if (gui.Pass != Pass.Pass2Render) return;
            gridWidth = Math.Max(1, gui.CurrentNode.InnerRect.W);
            gridHeight = Math.Max(1, gui.CurrentNode.InnerRect.H);
            gridScroll = gui.GetScrollState(gridId)?.ScrollOffset.Y ?? 0;
            GridInput(gui, clicked);
        }
    }

    bool GridRows(Gui gui)
    {
        var clicked = false;
        if (gridEntries.Count == 0) gui.DrawText("none", ThemeTokens.Current.Text(11), ThemeTokens.Current.InkDim);
        Spacer(gui, "assets/grid/top", gridWindow.FirstRow * cellHeight);
        for (var row = gridWindow.FirstRow; row < gridWindow.LastRow; row++)
        {
            using (gui.Node(-1, cellHeight, $"assets/grid/row/{row}").ExpandWidth().Direction(Axis.Horizontal)
                       .AlignContent(0, 0).Enter())
            {
                for (var column = 0; column < gridWindow.Columns; column++)
                {
                    var index = row * gridWindow.Columns + column;
                    if (index < gridEntries.Count) clicked |= GridCell(gui, gridEntries[index], index);
                }
            }
        }
        Spacer(gui, "assets/grid/bottom", (gridWindow.RowCount - gridWindow.LastRow) * cellHeight);
        return clicked;
    }

    void PrepareGrid()
    {
        if (queryDirty)
        {
            gridEntries = AssetQuery.Apply(entries, navigation.Current!, frameFilter, types, favorites!.Paths);
            gridIds = [.. gridEntries.Select(entry => entry.AbsolutePath)];
            queryDirty = false;
        }
        previewSize = ThemeTokens.Current.Scale(Math.Clamp(browserSettings.GridZoom, 32, 256));
        cellWidth = previewSize + ThemeTokens.Current.Scale(24);
        cellHeight = previewSize + ThemeTokens.Current.Scale(AssetQuery.IsActive(frameFilter) ? 56 : 36);
        gridWindow = AssetGridLayout.Calculate(gridEntries.Count, gridWidth, cellWidth, cellHeight,
            gridReset ? 0 : gridScroll, gridHeight);
    }

    void GridScroll(Gui gui)
    {
        var oldOffset = gui.GetScrollState(gridId)?.ScrollOffset ?? Vector2.Zero;
        gui.ScrollY();
        if (gui.Pass != Pass.Pass2Render) return;
        var zooming = ControlHeld(gui) && Math.Abs(gui.Input.MouseWheelDelta) > 0.01f
            && gui.GetInteractable().OnHover();
        if (zooming)
        {
            SetGridOffset(gui, oldOffset);
            SetZoom(browserSettings.GridZoom + (int)(gui.Input.MouseWheelDelta * 16));
        }
        if (gridReset)
        {
            SetGridOffset(gui, Vector2.Zero);
            gridReset = false;
        }
        if (gridReveal) RevealGridSelection(gui);
    }

    bool GridCell(Gui gui, AssetEntry entry, int index)
    {
        var theme = ThemeTokens.Current;
        using (gui.Node(cellWidth, cellHeight, "assets/tile/" + entry.AbsolutePath).Padding(4)
                   .Direction(Axis.Vertical).AlignContent(0, 0).Enter())
        {
            var clicked = false;
            if (gui.Pass == Pass.Pass2Render)
            {
                var selected = state.SelectedIds.Contains(entry.AbsolutePath);
                gui.DrawBackgroundRect(selected ? theme.AccentFill : gui.GetInteractable().OnHover() ? theme.Hover : theme.Panel, 3);
                clicked = TileInput(gui, entry, index);
                if (DragPayload(new TreeItem(entry.AbsolutePath, DisplayName(entry.AbsolutePath), 0, Tag: entry)) is { } payload)
                    gui.DragSource("assets/drag/" + entry.AbsolutePath, payload, keyboard: false);
            }
            if (entry.IsDirectory) GridDropTarget(gui, entry, "assets/drop/" + entry.AbsolutePath);
            using (gui.Node(-1, previewSize, "assets/preview/" + entry.AbsolutePath).ExpandWidth().AlignContent(0.5f, 0.5f).Enter())
                DrawPreview(gui, entry, previewSize);
            TileLabel(gui, entry);
            if (AssetQuery.IsActive(frameFilter))
                using (gui.Node(-1, theme.Scale(16)).ExpandWidth().Enter())
                {
                    gui.ClipContent();
                    gui.DrawText(Path.GetRelativePath(favoritesProject!, entry.ParentPath!), theme.Text(9), theme.InkDim);
                }
            gui.Tooltip(gui.CurrentNode, entry.AbsolutePath);
            return clicked;
        }
    }

    bool TileInput(Gui gui, AssetEntry entry, int index)
    {
        var interactable = gui.GetInteractable();
        if (interactable.OnClick(MouseButton.Right))
        {
            if (!state.SelectedIds.Contains(entry.AbsolutePath)) state.SelectedId = entry.AbsolutePath;
            menuEntry = entry;
            menuAt = pointer;
            menuOpen = true;
            gui.RequestFocus(gridId, FocusReason.Mouse);
            return true;
        }
        if (!interactable.OnClick(out var clicks)) return false;
        state.Select(gridIds[index], gridIds, ControlHeld(gui), ShiftHeld(gui));
        gui.RequestFocus(gridId, FocusReason.Mouse);
        if (clicks >= 2 && !ControlHeld(gui) && !ShiftHeld(gui)) Activate(entry);
        return true;
    }

    void TileLabel(Gui gui, AssetEntry entry)
    {
        using (gui.Node(-1, ThemeTokens.Current.Scale(24), "assets/label/" + entry.AbsolutePath).ExpandWidth().Enter())
        {
            if (state.EditingId == entry.AbsolutePath)
                TileRename(gui, entry);
            else
            {
                gui.ClipContent();
                gui.DrawText((favorites!.Paths.Contains(entry.AbsolutePath) ? EditorIcons.Star + " " : "") + DisplayName(entry.AbsolutePath),
                    ThemeTokens.Current.Text(11), ThemeTokens.Current.Ink,
                    wrapWidth: previewSize >= ThemeTokens.Current.Scale(96) ? cellWidth - 8 : 0, clip: true);
            }
        }
    }

    void TileRename(Gui gui, AssetEntry entry)
    {
        state.EditingText = gui.TextInput(state.EditingText, width: 0, height: ThemeTokens.Current.Scale(22),
            id: "assets/rename/" + entry.AbsolutePath, grabFocus: renameFocusRequested);
        if (gui.Pass != Pass.Pass2Render) return;
        renameFocusRequested = false;
        if (gui.Input.IsKeyPressed(KeyboardKey.Escape)) state.CancelRename();
        else if (gui.Input.IsKeyPressed(KeyboardKey.Enter))
        {
            Rename(new TreeItem(entry.AbsolutePath, "", 0, Tag: entry), state.EditingText);
            state.CancelRename();
            gui.RequestFocus(gridId);
        }
    }

    void GridInput(Gui gui, bool clicked)
    {
        gui.RegisterFocusable(claimsArrowKeys: true);
        if (!clicked && gui.GetInteractable().OnClick(MouseButton.Right)) OnEmptyClick(MouseButton.Right);
        if (!CanNavigateGrid(gui)) return;
        if (gui.Input.IsKeyPressed(KeyboardKey.Enter) && Selected is { } entry) Activate(entry);
        var delta = GridDelta(gui);
        if (delta == 0) return;
        var selected = gridIds.ToList().IndexOf(state.SelectedId ?? "");
        var index = Math.Clamp(selected + delta, 0, gridEntries.Count - 1);
        state.Select(gridIds[index], gridIds, ControlHeld(gui), ShiftHeld(gui));
        gridReveal = true;
    }

    bool CanNavigateGrid(Gui gui) => gui.HasFocus() && !gui.Focus.IsTextInputFocused && gridEntries.Count > 0;

    int GridDelta(Gui gui)
    {
        if (gui.Input.IsKeyPressed(KeyboardKey.Left)) return -1;
        if (gui.Input.IsKeyPressed(KeyboardKey.Right)) return 1;
        if (gui.Input.IsKeyPressed(KeyboardKey.Up)) return -gridWindow.Columns;
        if (gui.Input.IsKeyPressed(KeyboardKey.Down)) return gridWindow.Columns;
        return 0;
    }

    void RevealGridSelection(Gui gui)
    {
        gridReveal = false;
        var index = gridIds.ToList().IndexOf(state.SelectedId ?? "");
        if (index < 0) return;
        var top = index / gridWindow.Columns * cellHeight;
        var offset = gui.GetScrollState(gridId)?.ScrollOffset.Y ?? 0;
        if (top < offset) SetGridOffset(gui, new Vector2(0, top));
        else if (top + cellHeight > offset + gridHeight)
            SetGridOffset(gui, new Vector2(0, top + cellHeight - gridHeight));
    }

    static void SetGridOffset(Gui gui, Vector2 offset) =>
        gui.ScrollBy(gridId, offset - (gui.GetScrollState(gridId)?.ScrollOffset ?? Vector2.Zero));

    void Activate(AssetEntry entry)
    {
        if (entry.IsDirectory) { NavigateTo(entry.AbsolutePath); ClearFilters(); }
        else if (!entry.IsReadOnly) opener.Open(entry);
    }

    static bool ControlHeld(Gui gui) => gui.Input.IsKeyDown(KeyboardKey.LeftControl) || gui.Input.IsKeyDown(KeyboardKey.RightControl);

    static bool ShiftHeld(Gui gui) => gui.Input.IsKeyDown(KeyboardKey.LeftShift) || gui.Input.IsKeyDown(KeyboardKey.RightShift);

    static void Spacer(Gui gui, string id, float height)
    {
        if (height <= 0) return;
        using (gui.Node(-1, height, id).ExpandWidth().Enter()) { }
    }
}
