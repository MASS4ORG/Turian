namespace Gaya.Plugin.Turian;

/// <summary>
/// What the panel shows: the shared selection or the one it is locked on, and the form built for it, including the
/// subjects editor bricks describe through an inspector source.
/// </summary>
sealed partial class InspectorPanel
{
    object? lockedTarget;
    object? frameTarget;
    IReadOnlyList<Node> frameNodes = [];
    IReadOnlyList<Node> builtNodes = [];
    IReadOnlyList<Node> lockedNodes = [];
    int selectionComponents;
    IReadOnlyList<AssetInspection> frameAssets = [];
    IReadOnlyList<AssetInspection> builtAssets = [];
    IReadOnlyList<AssetInspection> lockedAssets = [];
    InspectorForm? subjectForm;
    IReadOnlyList<object> frameSubjects = [];
    IReadOnlyList<object> builtSubjects = [];
    IReadOnlyList<object> lockedSubjects = [];

    // The document that was active when the panel locked on subjects, whose closing releases the lock.
    IDocument? lockedDocument;

    /// <summary>
    /// Whether this instance keeps showing <see cref="lockedTarget"/> instead of following the shared
    /// selection, so one instance can stay put while another follows clicks.
    /// </summary>
    public bool Locked
    {
        get => lockedTarget is not null;
        set
        {
            var target = value ? Selected : null;
            if (target is not null && IsAlive(target))
            {
                Lock(target);
            }
            else
            {
                Unlock();
            }
        }
    }

    object? Selected => inspector.SelectedNode ?? inspector.SelectedObject;

    void Lock(object target)
    {
        lockedTarget = target;
        lockedNodes = inspector.SelectedNodes;
        lockedAssets = SelectedAssets();
        lockedSubjects = IsSubject(target) ? SelectedSubjects() : [];
        lockedDocument = lockedSubjects.Count > 0 ? documents?.Active : null;
    }

    void Unlock()
    {
        lockedTarget = null;
        lockedNodes = [];
        lockedAssets = [];
        lockedSubjects = [];
        lockedDocument = null;
    }

    object? FrameSelection(Gui gui)
    {
        // Both passes draw the same selection; clicks become visible in the next frame.
        if (gui.Pass == Pass.Pass1Build)
        {
            CaptureSelection();
        }
        return frameTarget;
    }

    void CaptureSelection()
    {
        ReleaseDeadLock();
        if (Locked)
        {
            frameNodes = lockedNodes;
            frameAssets = lockedAssets;
            frameSubjects = [.. lockedSubjects.Where(IsAlive)];
        }
        else
        {
            frameNodes = inspector.SelectedNodes;
            frameAssets = SelectedAssets();
            frameSubjects = SelectedSubjects();
        }

        var target = lockedTarget ?? Selected;
        frameTarget = target is null || IsAlive(target) ? target : frameSubjects.LastOrDefault();
    }

    bool IsSubject(object? target) => sources?.IsSubject(target) == true;

    bool IsAlive(object target) => sources?.IsAlive(target) != false;

    IReadOnlyList<AssetInspection> SelectedAssets() => [.. inspector.Selection.Objects.OfType<AssetInspection>()];

    IReadOnlyList<object> SelectedSubjects() =>
        [.. inspector.Selection.Objects.Where(item => IsSubject(item) && IsAlive(item))];

    // A lock on subjects ends with them: their document closed, they died or nothing describes them any more.
    void ReleaseDeadLock()
    {
        if (lockedSubjects.Count == 0)
        {
            return;
        }

        var closed = lockedDocument is not null && documents?.Documents.Contains(lockedDocument) != true;
        if (closed || !lockedSubjects.Any(subject => IsAlive(subject) && sources?.SourceFor(subject) is not null))
        {
            Locked = false;
        }
    }

    bool NeedsRebuild(object target, int components, int allComponents) => !ReferenceEquals(builtFor, target)
        || components != builtComponents || allComponents != selectionComponents
        || !builtNodes.SequenceEqual(frameNodes) || SubjectsChanged(target);

    bool SubjectsChanged(object target) => IsSubject(target)
        && (!builtSubjects.SequenceEqual(frameSubjects, ReferenceEqualityComparer.Instance)
            || subjectForm?.IsStale != false);

    void RebuildForm(object target, int components)
    {
        ReleaseSubjectForm();
        model = BuildModel(target);
        FoldSigningKeys();
        builtFor = target;
        builtComponents = components;
        selectionComponents = frameNodes.Sum(static node => node.Components.Count);
        builtNodes = frameNodes;
        builtSubjects = frameSubjects;
        FoldNewSections(target);
    }

    FormModel BuildModel(object target) => target switch
    {
        FormInspection inspection => inspection.Model,
        Node => InspectorForms.BuildForNodes(frameNodes, inspector.CreateMutationNotifier()),
        _ when IsSubject(target) => BuildSubjectForm(target),
        _ => InspectorForms.Build(target, _ => assets.AlterAssetForSelectedNode()),
    };

    // A component title not seen before starts folded when the setting says so; the node's own section (index 0) is
    // never one of them, and a subject's categories stay open. A title the user has toggled keeps what they left.
    void FoldNewSections(object target)
    {
        if (settings.AutoExpandComponents || IsSubject(target))
        {
            return;
        }

        foreach (var section in model.Sections.Skip(1))
        {
            collapsed.Add(section.Title);
        }
    }

    // Writes happen in the render pass, so that is when a group has anything to collect.
    IDisposable? GroupSubjectEdits(Gui gui, object target) =>
        gui.Pass == Pass.Pass2Render && IsSubject(target)
            ? sources!.GroupEdits(frameSubjects, $"Edit {frameSubjects.Count} Objects")
            : null;

    FormModel BuildSubjectForm(object target)
    {
        subjectForm = sources!.Build(frameSubjects, target);
        return subjectForm?.Model ?? FormModel.Empty;
    }

    // Lets go of the subjects, so a closed document's objects are not kept alive or listened to.
    void ReleaseSubjectForm()
    {
        if (subjectForm is null)
        {
            return;
        }

        subjectForm.Dispose();
        subjectForm = null;
        builtFor = null;
        builtSubjects = [];
        model = FormModel.Empty;
    }

    // A registry's signing keys start folded the first time they are shown; after that the user's choice stands.
    void FoldSigningKeys()
    {
        if (!foldedSigning && model.Sections.Any(static s => s.Title == BrickInspections.SigningTitle))
        {
            foldedSigning = true;
            _ = collapsed.Add(BrickInspections.SigningTitle);
        }
    }

    // Tags and layers belong to engine objects; a brick's subject is not reflected over.
    IReadOnlyList<string> ValidationWarnings(object target) =>
        IsSubject(target) ? [] : TagsAndLayersValidation.Warnings(target, layerFilter.Settings);

    bool IsMarked(FormField field, string member, InspectorPropertyBinding[] bindings) =>
        overrides.IsOverridden(field.Target, member) || bindings.Any(static binding => binding.IsSet);

    static InspectorPropertyBinding[] BindingsOf(FormField field) =>
        [.. field.Sources.Select(static source => source.Target).OfType<InspectorPropertyBinding>()];

    // A described property explains itself on hover and offers to drop the object's own value.
    void DecorateProperty(Gui gui, InspectorPropertyBinding[] bindings)
    {
        if (bindings.Length == 0)
        {
            return;
        }

        if (bindings[0].Property.Tooltip is { Length: > 0 } tooltip)
        {
            gui.Tooltip(gui.CurrentNode, tooltip);
        }

        if (gui.Pass == Pass.Pass2Render && bindings.Any(static binding => binding.CanReset && binding.IsSet)
            && gui.GetInteractable().OnClick(MouseButton.Right))
        {
            OpenOverrideMenu(gui, menu => menu.Item("Reset", () => Reset(bindings)));
        }
    }

    static void Reset(IEnumerable<InspectorPropertyBinding> bindings)
    {
        foreach (var binding in bindings.Where(static binding => binding.IsSet))
        {
            binding.Reset();
        }
    }
}
