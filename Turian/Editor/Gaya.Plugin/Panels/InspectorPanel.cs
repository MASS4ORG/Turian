namespace Gaya.Plugin.Turian;

/// <summary>
/// Edits the selected node: its own members, then one section per component, built by
/// <see cref="FormBuilder"/> and drawn by <see cref="FieldDrawers"/>. An asset selected in the
/// browser is edited here too — its data payload, or the import settings its importer declares.
/// </summary>
sealed class InspectorPanel(NodeInspectorController inspector, AssetManager assets,
    ReferencePicker references, AssetRevealService reveal, AssetInspectionService inspections,
    InspectorSettings settings, Vulkan vulkan, AssetPreviewCatalog previews) : IPanel, IDisposable
{
    readonly ReferenceDrawer referenceDrawer = new(references, inspector, reveal);
    readonly AssetPreviewView preview = new(vulkan, previews);

    static StudioTheme Theme => StudioTheme.Current;

    readonly HashSet<string> collapsed = [];

    FormModel model = FormModel.Empty;
    object? builtFor;
    int builtComponents;
    bool assetDirty;
    bool applyRequested;
    bool revertRequested;
    bool addOpen;
    Rect addAnchor;
    Vector2 addMenuAt;
    Component? removeRequest;
    object? lockedTarget;

    /// <summary>
    /// Whether this instance keeps showing <see cref="lockedTarget"/> instead of following the shared
    /// selection — Unity's Inspector lock, so one instance can stay put while another follows clicks.
    /// </summary>
    public bool Locked
    {
        get => lockedTarget is not null;
        set => lockedTarget = value ? inspector.SelectedNode ?? inspector.SelectedObject : null;
    }

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        // A node gets the node form — its own members plus one section per component. Anything else
        // the shell selects, such as the project settings, gets the plain object form. A locked
        // instance keeps whatever it was showing when it was locked, live selection notwithstanding.
        var target = lockedTarget ?? inspector.SelectedNode ?? inspector.SelectedObject;
        if (target is null)
        {
            return;
        }

        if (target is AssetInspection inspection)
        {
            RenderAsset(gui, inspection);
            return;
        }

        // Rebuilt when the selection changes, and when a component is added or removed: reflection is
        // cached per type, the form is not.
        var components = (target as Node)?.Components.Count ?? 0;
        if (gui.Pass == Pass.Pass1Build && (!ReferenceEquals(builtFor, target) || components != builtComponents))
        {
            model = target is Node node
                ? FormBuilder.BuildForNode(node, _ => assets.AlterAssetForSelectedNode())
                : FormBuilder.Build(target, _ => assets.AlterAssetForSelectedNode());
            builtFor = target;
            builtComponents = components;

            // A component title not seen before starts folded when the setting says so; the node's
            // own section (index 0) is never one of them — its header is always open. A title the
            // user has already toggled keeps whatever they left it at.
            if (!settings.AutoExpandComponents)
                foreach (var section in model.Sections.Skip(1))
                    collapsed.Add(section.Title);
            Log.Logger.LogDebug("node form: {Target} sections=[{Sections}]",
                target.GetType().Name,
                string.Join(", ", model.Sections.Select(s =>
                    $"{s.Title}:{s.BodyFields.Count}f/{s.Buttons.Count}b")));
        }

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(4f).Padding(6f, 4f).Enter())
        {
            gui.DropTarget<ScriptDragPayload>("inspector/component-drop",
                canAccept: payload => payload is ScriptDragPayload drop
                            && inspector.SelectedNode is not null
                            && ComponentRegistry.CanAddTo(inspector.SelectedNode, drop.ComponentType),
                onDrop: payload =>
                {
                    if (payload is ScriptDragPayload drop)
                        inspector.AddComponent(drop.ComponentType);
                });
            gui.ScrollY();

            // The node's own section is the header: always open, with the active toggle beside the
            // name. Components keep their fold.
            for (var i = 0; i < model.Sections.Count; i++)
                if (i == 0 && target is Node) RenderHeader(gui, model.Sections[i]);
                else RenderSection(gui, model.Sections[i], i);

            if (target is Node) RenderAddComponent(gui);
        }

        referenceDrawer.DrawPendingPicker(gui);
        DrawAddComponentMenu(gui);
        if (removeRequest is { } removing)
        {
            removeRequest = null;
            inspector.RemoveComponent(removing);
        }
    }

    /// <summary>
    /// An asset: the file name, then what there is to edit about it — a data asset's own class, the
    /// way a ScriptableObject is edited, or the settings its importer reads when it bakes the file.
    /// Edits are held until Apply, because applying one reimports the asset.
    /// </summary>
    void RenderAsset(Gui gui, AssetInspection inspection)
    {
        if (gui.Pass == Pass.Pass1Build && !ReferenceEquals(builtFor, inspection))
        {
            model = inspection.Target is null
                ? FormModel.Empty
                : FormBuilder.Build(inspection.Target, _ => assetDirty = true);
            builtFor = inspection;
            builtComponents = 0;
            assetDirty = false;
            var section = model.Sections.Count > 0 ? model.Sections[0] : null;
            Log.Logger.LogInformation("asset model built for {Path} ({Type}), targetNull={Null}, fields={Fields}, buttons={Buttons}",
                Path.GetFileName(inspection.AbsolutePath), inspection.Target?.GetType().FullName,
                inspection.Target is null, section?.BodyFields.Count ?? -1, section?.Buttons.Count ?? -1);
        }

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(4f).Padding(6f, 4f).Enter())
        {
            gui.ScrollY();

            using (gui.Node(-1, Theme.Scale(24f), "inspector/asset/name").ExpandWidth()
                       .Padding(6, 2).ContentAlignY(0.5f).Enter())
                gui.DrawText(Path.GetFileName(inspection.AbsolutePath), Theme.Text(13), Theme.Ink,
                    centerInRect: false);

            preview.Draw(gui, inspection.Metadata);

            if (inspection.Target is null)
            {
                gui.DrawText("This asset is used as it is: there is nothing to import.", Theme.Text(11),
                    Theme.InkDim, centerInRect: false);
                return;
            }

            using (gui.Node(-1, Theme.Scale(22f), "inspector/asset/header").ExpandWidth()
                       .Padding(6, 0).ContentAlignY(0.5f).Enter())
            {
                if (gui.Pass == Pass.Pass2Render) gui.DrawBackgroundRect(Theme.Panel, 3);
                gui.DrawText(inspection.Title, Theme.Text(12), Theme.Ink, centerInRect: false);
            }

            using (gui.Node(-1, -1, "inspector/asset/fields").ExpandWidth().Direction(Axis.Vertical)
                       .Padding(8, 2).Gap(2f).Enter())
            {
                var fields = model.Sections[0].BodyFields;
                for (var f = 0; f < fields.Count; f++)
                    FieldDrawers.Draw(gui, fields[f], $"inspector/asset/field{f}", referenceDrawer, collapsed);
                DrawButtons(gui, model.Sections[0].Buttons, "inspector/asset/button");
            }

            RenderAssetActions(gui);
        }

        referenceDrawer.DrawPendingPicker(gui);

        if (applyRequested)
        {
            applyRequested = false;
            if (inspections.Apply(inspection)) assetDirty = false;
        }

        if (revertRequested)
        {
            revertRequested = false;
            inspector.Select(inspections.Inspect(inspection.AbsolutePath));
        }
    }

    /// <summary>Apply writes the edit and reimports; revert re-reads what is on disk.</summary>
    void RenderAssetActions(Gui gui)
    {
        using (gui.Node(-1, Theme.Scale(28f), "inspector/asset/actions").ExpandWidth()
                   .Direction(Axis.Horizontal).Padding(8, 4).Gap(6f).Enter())
        {
            using (gui.Node().Expand().Enter()) { }

            if (TextButton(gui, "Revert", "inspector/asset/revert", assetDirty)) revertRequested = true;
            if (TextButton(gui, "Apply", "inspector/asset/apply", assetDirty)) applyRequested = true;
        }
    }

    /// <summary>A labelled button, dimmed and inert while it has nothing to do.</summary>
    static bool TextButton(Gui gui, string label, string id, bool enabled)
    {
        using (gui.Node(Theme.Scale(70f), Theme.Scale(20f), id).BlockInput().ContentAlignX(0.5f)
                   .ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = enabled && interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render) gui.DrawBackgroundRect(hot ? Theme.Border : Theme.Panel, 3);

            gui.DrawText(label, Theme.Text(12), enabled ? Theme.Ink : Theme.InkDim);

            return gui.Pass == Pass.Pass2Render && hot && interactable.OnClick();
        }
    }

    /// <summary>
    /// The node's identity line — active toggle and name together — followed by its remaining members.
    /// </summary>
    void RenderHeader(Gui gui, FormSection section)
    {
        var active = section.EnabledField;
        var name = section.Fields.FirstOrDefault(f => f.ValueType == typeof(string) && f.Name == "Name");

        using (gui.Node(-1, Theme.Scale(24f), "inspector/header").ExpandWidth().Direction(Axis.Horizontal)
                   .Padding(6, 2).Gap(6f).Enter())
        {
            if (active is not null) Toggle(gui, active, "inspector/header/active");

            if (name is not null)
            {
                var current = name.GetValue() as string ?? string.Empty;
                var edited = gui.TextInput(current, width: 0, height: Theme.Scale(20f),
                    fontSize: Theme.Text(13),
                    backgroundColor: Theme.Field, borderColor: Theme.Border, textColor: Theme.Ink, padding: 4,
                    id: "inspector/header/name");

                if (!string.Equals(edited, current, StringComparison.Ordinal)) name.SetValue(edited);
            }
            else
            {
                gui.DrawText(section.Title, Theme.Text(13), Theme.Ink, centerInRect: false);
            }
        }

        using (gui.Node(-1, -1, "inspector/header/fields").ExpandWidth().Direction(Axis.Vertical)
                   .Padding(8, 2).Gap(2f).Enter())
        {
            var rest = section.BodyFields.Where(f => f != name).ToList();
            for (var f = 0; f < rest.Count; f++)
                FieldDrawers.Draw(gui, rest[f], $"inspector/header/field{f}", referenceDrawer, collapsed);
            DrawButtons(gui, section.Buttons, "inspector/header/button");
        }
    }

    void RenderSection(Gui gui, FormSection section, int index)
    {
        var isOpen = !collapsed.Contains(section.Title);

        using (gui.Node(-1, Theme.Scale(22f), $"inspector/section{index}").ExpandWidth()
                   .Direction(Axis.Horizontal)
                   .Padding(6, 0).Gap(4f).ContentAlignY(0.5f).Enter())
        {
            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawBackgroundRect(Theme.Panel, 3);
                if (gui.GetInteractable().OnClick()) Fold(section.Title);
            }

            using (gui.Node(10, Theme.Scale(22f), $"inspector/section{index}/arrow")
                       .ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
                FieldDrawers.DrawArrow(gui, isOpen);

            // The component's own on/off switch, beside its name as Unity puts it. It blocks the
            // header behind it so ticking the box does not also fold the section.
            if (section.EnabledField is { } enabled) Toggle(gui, enabled, $"inspector/section{index}/enabled");

            gui.DrawText(section.Title, Theme.Text(12), Theme.Ink, centerInRect: false);

            using (gui.Node().Expand().Enter()) { }

            if (section is { Removable: true, Target: Component component }
                && FieldDrawers.SmallButton(gui, "×", $"inspector/section{index}/remove"))
                removeRequest = component;
        }

        if (!isOpen) return;

        using (gui.Node(-1, -1, $"inspector/section{index}/fields").ExpandWidth()
                   .Direction(Axis.Vertical).Padding(8, 2).Gap(2f).Enter())
        {
            if (section.BodyFields.Count > 0)
            {
                for (var f = 0; f < section.BodyFields.Count; f++)
                    FieldDrawers.Draw(gui, section.BodyFields[f], $"inspector/section{index}/field{f}",
                        referenceDrawer, collapsed);
            }
            else if (section.Buttons.Count == 0)
            {
                gui.DrawText("No editable members.", Theme.Text(11), Theme.InkDim, centerInRect: false);
            }

            DrawButtons(gui, section.Buttons, $"inspector/section{index}/button");
        }
    }

    /// <summary>
    /// The <c>[Button]</c> methods under a section's fields, each invoking its action the frame it is
    /// pressed.
    /// </summary>
    static void DrawButtons(Gui gui, IReadOnlyList<InspectorButton> buttons, string id)
    {
        for (var b = 0; b < buttons.Count; b++)
        {
            var buttonId = $"{id}{b}";
            if (FieldDrawers.TextButton(gui, buttons[b].Label, buttonId))
                buttons[b].Invoke();
        }
    }

    /// <summary>A bool field as a checkbox that swallows the click, for use inside a clickable header.</summary>
    static void Toggle(Gui gui, FormField field, string id)
    {
        using (gui.Node(Theme.Scale(16f), Theme.Scale(16f), id).BlockInput().Enter())
        {
            var current = field.GetValue() is true;
            var next = gui.Checkbox(current, size: Theme.Scale(14f));

            if (gui.Pass == Pass.Pass2Render && next != current) field.SetValue(next);
        }
    }

    void RenderAddComponent(Gui gui)
    {
        using (gui.Node(-1, Theme.Scale(26f), "inspector/addComponent").ExpandWidth()
                   .Padding(24, 3).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawBackgroundRect(hot ? Theme.Border : Theme.Panel, 3);
                addAnchor = gui.CurrentNode.Rect;
            }

            gui.DrawText("Add Component", Theme.Text(12), Theme.Ink);

            if (gui.Pass == Pass.Pass2Render && hot && interactable.OnClick())
            {
                addMenuAt = new Vector2(addAnchor.X, addAnchor.Y + addAnchor.H);
                addOpen = !addOpen;
            }
        }
    }

    /// <summary>
    /// The component menu, nested the way each type's <c>[ComponentContextMenu]</c> path describes it.
    /// Drawn at the top level of the panel rather than under the button: a menu opened from inside the
    /// inspector's scrolled subtree is clipped by it.
    /// </summary>
    void DrawAddComponentMenu(Gui gui) =>
        gui.CascadeMenu(ref addOpen, addMenuAt, BuildAddComponentMenu);

    void BuildAddComponentMenu(FlyoutBuilder menu) =>
        BuildComponentLevel(menu, [.. inspector.GetAvailableComponents()], depth: 0);

    /// <summary>
    /// Emits one level of the component menu: types whose path ends here become items, and the rest are
    /// gathered by their next path segment into a submenu that recurses.
    /// </summary>
    void BuildComponentLevel(FlyoutBuilder menu, IReadOnlyList<ComponentTypeDescriptor> candidates,
        int depth)
    {
        foreach (var candidate in candidates.Where(c => Segments(c).Length == depth + 1)
                     .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var type = candidate.ComponentType;
            menu.Item(candidate.DisplayName, () => inspector.AddComponent(type));
        }

        foreach (var group in candidates.Where(c => Segments(c).Length > depth + 1)
                     .GroupBy(c => Segments(c)[depth], StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var nested = group.ToList();
            menu.Submenu(group.Key, sub => BuildComponentLevel(sub, nested, depth + 1));
        }
    }

    static string[] Segments(ComponentTypeDescriptor candidate) =>
        candidate.MenuPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    void Fold(string title)
    {
        if (!collapsed.Add(title)) collapsed.Remove(title);
    }

    /// <inheritdoc/>
    public void Dispose() => preview.Dispose();
}
