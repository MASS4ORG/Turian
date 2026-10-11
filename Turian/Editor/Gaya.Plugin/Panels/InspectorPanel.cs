namespace Gaya.Plugin.Turian;

/// <summary>
/// Edits the selected node: its own members, then one section per component, built by
/// <see cref="InspectorForms"/> and drawn by <see cref="FormRenderer"/>. An asset selected in the
/// browser is edited here too — its data payload, or the import settings its importer declares.
/// </summary>
sealed partial class InspectorPanel(NodeInspectorController inspector, AssetManager assets,
    ReferencePicker references, AssetRevealService reveal, AssetInspectionService inspections,
    InspectorSettings settings, Vulkan vulkan, AssetPreviewCatalog previews, UndoService undo, AssetAutoSave autoSave,
    PrefabOverrideOperations prefabOperations, PrefabStage prefabStage, AssetDatabase database, LayerFilter? layers = null)
    : IPanel, IDisposable
{
    readonly ReferenceDrawer referenceDrawer = new(references, inspector, reveal);
    readonly LayerFilter layerFilter = layers ?? new LayerFilter();
    readonly AssetPreviewView preview = new(vulkan, database, previews);
    readonly PrefabOverrideTracker overrides =
        new(id => PrefabInstances.ReadPrefabJson(database, id));
    bool trackingEdits;
    IReadOnlyList<string> validationWarnings = [];

    static ThemeTokens Theme => ThemeTokens.Current;

    readonly HashSet<string> collapsed = [];
    readonly FormDrawers drawers = new(TurianForms.Drawers);
    IDisposable? referenceRegistration;
    FormRenderContext? formContext;
    bool fieldOverridden;

    FormModel model = FormModel.Empty;
    object? builtFor;
    int builtComponents;
    bool assetDirty;
    bool applyRequested;
    bool revertRequested;
    bool addOpen;
    Rect addAnchor;
    Vector2 addMenuAt;
    bool overrideMenuOpen;
    Vector2 overrideMenuAt;
    Action<FlyoutBuilder>? overrideMenu;
    Component? removeRequest;
    object? lockedTarget;
    object? frameTarget;
    IReadOnlyList<Node> frameNodes = [];
    IReadOnlyList<Node> builtNodes = [];
    IReadOnlyList<Node> lockedNodes = [];
    int selectionComponents;
    IReadOnlyList<AssetInspection> frameAssets = [];
    IReadOnlyList<AssetInspection> builtAssets = [];
    IReadOnlyList<AssetInspection> lockedAssets = [];

    /// <summary>
    /// Whether this instance keeps showing <see cref="lockedTarget"/> instead of following the shared
    /// selection, so one instance can stay put while another follows clicks.
    /// </summary>
    public bool Locked
    {
        get => lockedTarget is not null;
        set
        {
            lockedTarget = value ? inspector.SelectedNode ?? inspector.SelectedObject : null;
            lockedNodes = value ? inspector.SelectedNodes : [];
            lockedAssets = value ? [.. inspector.Selection.Objects.OfType<AssetInspection>()] : [];
        }
    }

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        var target = FrameSelection(gui);
        if (target is null)
        {
            return;
        }

        if (Locked) RecordLockedSelection();

        if (target is AssetInspection inspection)
        {
            if (inspection.Target is { } payload) DrawValidation(gui, payload);
            RenderAsset(gui, inspection);
            return;
        }

        PrepareForm(gui, target);
        DrawValidation(gui, target);

        RenderForm(gui, target);

        referenceDrawer.DrawPendingPicker(gui);
        DrawAddComponentMenu(gui);
        gui.CascadeMenu(ref overrideMenuOpen, overrideMenuAt, menu => overrideMenu?.Invoke(menu));
        if (removeRequest is { } removing)
        {
            removeRequest = null;
            RemoveComponents(removing);
        }
    }

    void RecordLockedSelection()
    {
        foreach (var node in frameNodes)
        {
            undo.RecordObject(node, "Edit Selection");
            foreach (var component in node.Components) undo.RecordObject(component, "Edit Selection");
        }
        undo.RecordInspections(frameAssets, "Edit Selection");
    }

    /// <summary>
    /// The panel's form context: kept for the panel's lifetime so fold state survives frames, with references drawn
    /// through this panel's picker and the prefab-override mark of the field being drawn.
    /// </summary>
    FormRenderContext FormContext => formContext ??= CreateFormContext();

    FormRenderContext CreateFormContext()
    {
        drawers.Add(TagDrawer.Handles, new TagDrawer(layerFilter, undo));
        drawers.Add(LayerDrawer.Handles, new LayerDrawer(layerFilter, undo));
        referenceRegistration = drawers.Add(ReferenceDrawer.Handles, referenceDrawer);
        return new FormRenderContext
        {
            Drawers = drawers,
            Collapsed = collapsed,
            CanInline = TurianForms.CanInline,
            IsModified = _ => fieldOverridden,
        };
    }

    object? FrameSelection(Gui gui)
    {
        // Both passes draw the same selection; clicks become visible in the next frame.
        if (gui.Pass == Pass.Pass1Build)
        {
            frameTarget = lockedTarget ?? inspector.SelectedNode ?? inspector.SelectedObject;
            frameNodes = Locked ? lockedNodes : inspector.SelectedNodes;
            frameAssets = Locked ? lockedAssets : [.. inspector.Selection.Objects.OfType<AssetInspection>()];
        }
        return frameTarget;
    }

    void PrepareForm(Gui gui, object target)
    {
        if (!trackingEdits)
        {
            assets.AssetAltered += OnAssetAltered;
            trackingEdits = true;
        }

        overrides.Track(frameNodes.Count > 1 ? null : target as Node);
        var components = (target as Node)?.Components.Count ?? 0;
        var allComponents = frameNodes.Sum(node => node.Components.Count);
        if (gui.Pass == Pass.Pass1Build && NeedsRebuild(target, components, allComponents))
            RebuildForm(target, components);
    }

    bool NeedsRebuild(object target, int components, int allComponents) => !ReferenceEquals(builtFor, target)
        || components != builtComponents || allComponents != selectionComponents || !builtNodes.SequenceEqual(frameNodes);

    void RenderForm(Gui gui, object target)
    {
        using var form = gui.Node().Expand().Direction(Axis.Vertical).Gap(4f).Padding(6f, 4f).Enter();
        TurianForms.ApplyStyle(gui);
        gui.DropTarget<ScriptDragPayload>("inspector/component-drop",
            canAccept: CanDropScript, onDrop: DropScript);
        gui.ScrollY();

        // The node's own section is the header: always open, with the active toggle beside the
        // name. Components keep their fold.
        for (var i = 0; i < model.Sections.Count; i++)
        {
            if (i == 0 && target is Node) RenderHeader(gui, model.Sections[i]);
            else RenderSection(gui, model.Sections[i], i);

            if (i == 0 && target is Node node && ReferenceEquals(node, overrides.InstanceRoot)) RenderPrefabBar(gui, node);
        }

        if (target is Node) RenderAddComponent(gui);
    }

    void DrawValidation(Gui gui, object target)
    {
        if (gui.Pass == Pass.Pass1Build)
            validationWarnings = TagsAndLayersValidation.Warnings(target, layerFilter.Settings);
        for (var i = 0; i < validationWarnings.Count; i++)
            using (gui.Node(-1, Theme.Scale(24f), $"inspector/layers/warning/{i}").ExpandWidth().Enter())
                gui.DrawText(validationWarnings[i], Theme.Text(11f), Theme.InkDim);
    }

    void RebuildForm(object target, int components)
    {
        model = target switch
        {
            FormInspection inspection => inspection.Model,
            Node => InspectorForms.BuildForNodes(frameNodes, inspector.CreateMutationNotifier()),
            _ => InspectorForms.Build(target, _ => assets.AlterAssetForSelectedNode()),
        };
        builtFor = target;
        builtComponents = components;
        selectionComponents = frameNodes.Sum(node => node.Components.Count);
        builtNodes = frameNodes;

        // A component title not seen before starts folded when the setting says so; the node's
        // own section (index 0) is never one of them — its header is always open. A title the
        // user has already toggled keeps whatever they left it at.
        if (!settings.AutoExpandComponents)
            foreach (var section in model.Sections.Skip(1))
                collapsed.Add(section.Title);
    }

    bool CanDropScript(ScriptDragPayload drop) =>
        frameNodes.Count > 0 && frameNodes.All(node => ComponentRegistry.CanAddTo(node, drop.ComponentType));

    void DropScript(ScriptDragPayload drop) => AddComponent(drop.ComponentType);

    void AddComponent(Type type)
    {
        undo.BeginGesture();
        try
        {
            foreach (var node in frameNodes) undo.RecordObject(node, "Add Component");
            inspector.AddComponents(frameNodes, type);
        }
        finally { undo.EndGesture(); }
    }

    void RemoveComponents(Component representative)
    {
        undo.BeginGesture();
        try
        {
            foreach (var node in frameNodes) undo.RecordObject(node, "Remove Component");
            inspector.RemoveComponents(frameNodes, representative);
        }
        finally { undo.EndGesture(); }
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
            // The name box and the active toggle cannot turn bold, so their overrides mark the header's margin.
            RenderHeaderOverride(gui, section.Target);

            if (active is not null) Toggle(gui, active, "inspector/header/active");

            if (name is not null) RenderName(gui, name);
            else
            {
                gui.DrawText(section.Title, Theme.Text(13), Theme.Ink, centerInRect: false);
            }
            if (frameNodes.Count > 1) gui.DrawText($"{frameNodes.Count} Objects", Theme.Text(11), Theme.InkDim);
        }

        using (gui.Node(-1, -1, "inspector/header/fields").ExpandWidth().Direction(Axis.Vertical)
                   .Padding(8, 2).Gap(2f).Enter())
        {
            var rest = section.BodyFields.Where(f => f != name).ToList();
            for (var f = 0; f < rest.Count; f++)
                DrawField(gui, rest[f], $"inspector/header/field{f}");
            DrawButtons(gui, section.Buttons, "inspector/header/button");
        }
    }

    void RenderHeaderOverride(Gui gui, object target)
    {
        if (gui.Pass != Pass.Pass2Render) return;
        if (!overrides.IsOverridden(target, nameof(Node.Name)) && !overrides.IsOverridden(target, nameof(Node.IsActive)))
            return;
        var rect = gui.CurrentNode.Rect;
        gui.DrawRect(new Rect(rect.X, rect.Y + 2f, 2f, rect.H - 4f), Theme.Accent);
    }

    static void RenderName(Gui gui, FormField name)
    {
        var current = name.HasMixedValue ? string.Empty : name.GetValue() as string ?? string.Empty;
        var edited = gui.TextInput(current, width: 0, height: Theme.Scale(20f), fontSize: Theme.Text(13),
            padding: 4,
            id: "inspector/header/name", placeholder: name.HasMixedValue ? "—" : "");
        if (gui.Pass == Pass.Pass2Render && !string.Equals(edited, current, StringComparison.Ordinal))
            name.SetValue(edited);
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
                var header = gui.GetInteractable();
                if (header.OnClick()) Fold(section.Title);
                if (header.OnClick(MouseButton.Right) && section.Target is Component clicked)
                    OpenComponentMenu(gui, clicked);
            }

            using (gui.Node(10, Theme.Scale(22f), $"inspector/section{index}/arrow")
                       .ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
                FormControls.FoldArrow(gui, isOpen);

            // The component's own on/off switch, beside its name. It blocks the
            // header behind it so ticking the box does not also fold the section.
            if (section.EnabledField is { } enabled) Toggle(gui, enabled, $"inspector/section{index}/enabled");

            // A component the instance added reads "+ Title"; one with overridden values has a bold title.
            var title = overrides.IsAdded(section.Target) ? $"+ {section.Title}" : section.Title;
            var overridden = section.Target is IdObject owner && overrides.Diff?.HasOverrides(owner.Id) == true;
            gui.DrawText(title, Theme.Text(12), Theme.Ink, centerInRect: false,
                effects: FormControls.Emphasis(overridden, Theme.Ink));

            using (gui.Node().Expand().Enter()) { }

            if (section is { Removable: true, Target: Component component }
                && FormControls.SmallButton(gui, "×", $"inspector/section{index}/remove"))
                removeRequest = component;
        }

        if (!isOpen) return;

        using (gui.Node(-1, -1, $"inspector/section{index}/fields").ExpandWidth()
                   .Direction(Axis.Vertical).Padding(8, 2).Gap(2f).Enter())
        {
            if (section.BodyFields.Count > 0)
            {
                for (var f = 0; f < section.BodyFields.Count; f++)
                    DrawField(gui, section.BodyFields[f], $"inspector/section{index}/field{f}");
            }

            DrawButtons(gui, section.Buttons, $"inspector/section{index}/button");
        }
    }

    /// <summary>
    /// Draws a field of the selected node, marked when a prefab instance overrides it; right-clicking a marked field
    /// offers to revert it or apply it to the prefab.
    /// </summary>
    void DrawField(Gui gui, FormField field, string id)
    {
        var member = field.CollectionMember ?? field.Name;
        fieldOverridden = overrides.IsOverridden(field.Target, member);
        try
        {
            using (gui.Node(-1, -1, $"{id}/prefab").ExpandWidth().Direction(Axis.Vertical).Gap(2f).Enter())
            {
                if (fieldOverridden && gui.Pass == Pass.Pass2Render && field.Target is IdObject target
                    && gui.GetInteractable().OnClick(MouseButton.Right))
                {
                    OpenOverrideMenu(gui, menu =>
                    {
                        menu.Item("Revert", () => Edited(prefabOperations.RevertMember(target, member)));
                        menu.Item("Apply to Prefab", () => Edited(prefabOperations.ApplyMember(target, member)));
                    });
                }

                gui.FormField(field, id, FormContext);
            }
        }
        finally
        {
            fieldOverridden = false;
        }
    }

    void OnAssetAltered(Asset asset) => overrides.Invalidate();

    /// <summary>
    /// The prefab bar of an instance root: which prefab it comes from, a way into prefab mode, and Revert All / Apply
    /// All once the instance differs from its prefab.
    /// </summary>
    void RenderPrefabBar(Gui gui, Node instance)
    {
        var differs = overrides.Diff is { IsEmpty: false };
        var prefabId = instance.PrefabInstance?.Source.AssetId ?? Guid.Empty;
        var name = database.TryGetAsset(prefabId, out var record) && record is not null
            ? Path.GetFileNameWithoutExtension(record.SourceRelativePath)
            : "Missing Prefab";

        using (gui.Node(-1, Theme.Scale(26f), "inspector/prefab").ExpandWidth().Direction(Axis.Horizontal)
                   .Padding(6, 3).Gap(6f).ContentAlignY(0.5f).Enter())
        {
            if (gui.Pass == Pass.Pass2Render) gui.DrawBackgroundRect(Theme.Panel, 3);

            gui.DrawText(EditorIcons.Cube, Theme.Text(12), Theme.Accent);
            gui.DrawText(name, Theme.Text(12), Theme.Ink, centerInRect: false);
            using (gui.Node().Expand().Enter()) { }

            if (TextButton(gui, "Open", "inspector/prefab/open", record is not null)) prefabStage.OpenPrefab(instance);
            if (TextButton(gui, "Revert All", "inspector/prefab/revert", differs))
            {
                prefabOperations.RevertAll(instance);
                Edited(true);
            }

            if (TextButton(gui, "Apply All", "inspector/prefab/apply", differs))
                Edited(prefabOperations.ApplyAll(instance));
        }
    }

    /// <summary>
    /// A component's menu: moving it among the node's components, then its prefab overrides. A component a prefab
    /// provides keeps the prefab's order, as in Unity.
    /// </summary>
    void OpenComponentMenu(Gui gui, Component component)
    {
        var added = overrides.IsAdded(component);
        var prefabOwned = overrides.InstanceRoot is not null && !added;
        var overridden = !added && overrides.Diff?.HasOverrides(component.Id) == true;
        var index = component.Node?.Components.IndexOf(component) ?? -1;
        var count = component.Node?.Components.Count ?? 0;

        OpenOverrideMenu(gui, menu =>
        {
            menu.Item("Move Up", () => MoveComponent(component, up: true), enabled: !prefabOwned && index > 0);
            menu.Item("Move Down", () => MoveComponent(component, up: false),
                enabled: !prefabOwned && index >= 0 && index < count - 1);
            if (!added && !overridden) return;

            menu.Separator();
            menu.Item(added ? "Remove Added Component" : "Revert Component", () =>
            {
                prefabOperations.RevertComponent(component);
                Edited(true);
            });
            if (!added)
            {
                menu.Item("Apply Component to Prefab", () =>
                {
                    prefabOperations.ApplyComponent(component);
                    Edited(true);
                });
            }
        });
    }

    void MoveComponent(Component component, bool up)
    {
        undo.BeginGesture();
        try
        {
            foreach (var node in frameNodes)
                undo.RecordObject(node, up ? "Move Component Up" : "Move Component Down");
            inspector.MoveComponents(frameNodes, component, up);
        }
        finally { undo.EndGesture(); }
    }

    void OpenOverrideMenu(Gui gui, Action<FlyoutBuilder> build)
    {
        overrideMenu = build;
        overrideMenuAt = gui.Input.MousePosition;
        overrideMenuOpen = true;
    }

    // An override action changes values outside the form, so the marks are compared again.
    void Edited(bool changed)
    {
        if (changed) overrides.Invalidate();
    }

    void OnAssetEdited(AssetInspection inspection)
    {
        assetDirty = true;
        autoSave.MarkChanged(inspection);
        undo.MarkAltered();
    }


    /// <summary>
    /// The <c>[Button]</c> methods under a section's fields, each invoking its action the frame it is
    /// pressed.
    /// </summary>
    static void DrawButtons(Gui gui, IReadOnlyList<Button> buttons, string id)
    {
        for (var b = 0; b < buttons.Count; b++)
        {
            var buttonId = $"{id}{b}";
            if (FormControls.TextButton(gui, buttons[b].Label, buttonId, buttons[b].IsEnabled))
                buttons[b].Invoke();
        }
    }

    /// <summary>A bool field as a checkbox that swallows the click, for use inside a clickable header.</summary>
    static void Toggle(Gui gui, FormField field, string id)
    {
        using (gui.Node(Theme.Scale(16f), Theme.Scale(16f), id).BlockInput().Enter())
        {
            var current = !field.HasMixedValue && field.GetValue() is true;
            var next = gui.Checkbox(current, size: Theme.Scale(14f), mixed: field.HasMixedValue,
                enabled: !field.IsReadOnly);

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
        BuildComponentLevel(menu, [.. inspector.GetAvailableComponents(targets: frameNodes)], depth: 0);

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
            menu.Item(candidate.DisplayName, () => AddComponent(type));
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
    public void Dispose()
    {
        assets.AssetAltered -= OnAssetAltered;
        referenceRegistration?.Dispose();
        preview.Dispose();
    }
}
