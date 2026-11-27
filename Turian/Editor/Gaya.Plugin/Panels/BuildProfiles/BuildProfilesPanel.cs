namespace Gaya.Plugin.Turian;

/// <summary>
/// The Build Profiles window, Unity's counterpart: the project's registered profiles on the left with the active one
/// marked, and the selected profile's properties on the right, edited through the inspector's forms and saved as they
/// change. Profiles are added, switched to and built from here.
/// </summary>
sealed partial class BuildProfilesPanel(SettingsService settings, AssetInspectionService inspections, AssetAutoSave autoSave,
    UndoService undo, ReferenceDrawer references, BuildManager build, BackgroundTaskRunner tasks, UnsavedWork unsaved)
    : IPanel, IDisposable
{
    const float listWidth = 220f;
    const float rowHeight = 34f;
    const string undoLabel = "Edit Build Profile";

    readonly FormDrawers drawers = new(TurianForms.Drawers);
    readonly HashSet<string> collapsed = [];
    IDisposable? referenceRegistration;
    FormRenderContext? formContext;

    IReadOnlyList<BuildProfileListing> profiles = [];
    (string Project, DateTime Written)? listedFrom;
    Guid selected;
    AssetInspection? inspection;
    FormModel model = FormModel.Empty;
    Action? pending;
    bool addOpen;
    Vector2 addAt;

    static ThemeTokens Theme => ThemeTokens.Current;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);
        if (settings.Settings is not { } project)
        {
            return;
        }

        if (gui.Pass == Pass.Pass1Build)
        {
            Refresh(project);
        }

        using (gui.Node().Expand().Direction(Axis.Horizontal).Gap(Theme.Gap).Padding(10f, 8f).Enter())
        {
            List(gui);
            Details(gui);
        }

        gui.CascadeMenu(ref addOpen, addAt, menu => PopulateAddMenu(menu, project, BuildPlatforms.All()),
            id: "buildProfiles/add-menu");
        references.DrawPendingPicker(gui);

        if (gui.Pass != Pass.Pass2Render || pending is not { } action)
        {
            return;
        }

        pending = null;
        action();
    }

    /// <summary>
    /// Re-reads the profiles when another project opened or the project asset changed, which adding, switching and
    /// creating a profile from the asset browser all do.
    /// </summary>
    void Refresh(AppSettings project)
    {
        var path = ProjectAsset.PathIn(project.AssetsAbsoluteDir);
        var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        var stamp = (project.ProjectAbsoluteDir, written);
        if (listedFrom == stamp)
        {
            return;
        }

        listedFrom = stamp;
        profiles = BuildProfiles.List(project);
        if (profiles.All(p => p.Id != selected))
        {
            Select((profiles.FirstOrDefault(static p => p.IsActive) ?? profiles.FirstOrDefault())?.Id ?? Guid.Empty);
        }
    }

    void Select(Guid id)
    {
        autoSave.Flush(force: true);
        selected = id;
        inspection = profiles.FirstOrDefault(p => p.Id == id) is { } listing
            ? inspections.Inspect(listing.Profile.Path)
            : null;
        model = inspection?.Target is { } target
            ? InspectorForms.Build(target, _ => Edited())
            : FormModel.Empty;
    }

    void Edited()
    {
        if (inspection is null)
        {
            return;
        }

        autoSave.MarkChanged(inspection);
        undo.MarkAltered();
    }

    void List(Gui gui)
    {
        using (gui.Node(Theme.Scale(listWidth), -1, "buildProfiles/list").ExpandHeight().Direction(Axis.Vertical)
                   .Gap(2f).Enter())
        {
            using (gui.Node().Expand().Direction(Axis.Vertical).Gap(2f).Enter())
            {
                gui.ScrollY();
                if (profiles.Count == 0)
                {
                    Line(gui, "No build profiles", Theme.InkDim);
                }

                foreach (var profile in profiles)
                {
                    Row(gui, profile);
                }
            }

            if (StudioControls.SmallTextButton(gui, "Add Build Profile", "buildProfiles/add", Theme.Scale(listWidth)))
            {
                addAt = gui.Input.MousePosition;
                addOpen = true;
            }
        }
    }

    void Row(Gui gui, BuildProfileListing profile)
    {
        var id = $"buildProfiles/row/{profile.Id}";
        var height = Theme.Scale(rowHeight);

        using (gui.Node(-1, height, id).ExpandWidth().Direction(Axis.Horizontal).Gap(6f).Padding(6f, 0f)
                   .AlignContentY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            if (gui.Pass == Pass.Pass2Render)
            {
                if (profile.Id == selected)
                {
                    gui.DrawBackgroundRect(Theme.Hover, 3f);
                }
                else if (interactable.OnHover())
                {
                    gui.DrawBackgroundRect(Theme.Chrome, 3f);
                }

                if (interactable.OnClick())
                {
                    pending = () => Select(profile.Id);
                }
            }

            using (gui.Node(-1, height, $"{id}/name").Expand().Direction(Axis.Vertical).Gap(2f).AlignContentY(0.5f)
                       .Enter())
            {
                gui.ClipContent();
                gui.DrawText(NameOf(profile), Theme.Text(12f), Theme.Ink, centerInRect: false);
                gui.DrawText(Summary(profile.Profile.Profile), Theme.Text(10f), Theme.InkDim, centerInRect: false);
            }

            if (profile.IsActive)
            {
                gui.DrawText("Active", Theme.Text(10f), Theme.Accent);
            }
        }
    }

    /// <summary>The selected profile's own name while it is edited, so a rename shows in the list at once.</summary>
    string NameOf(BuildProfileListing profile) =>
        profile.Id == selected && inspection?.Target is BuildProfile { Name: { } name }
                               && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : profile.Profile.Name;

    static string Summary(BuildProfile profile) =>
        $"{BuildPlatformName(profile.Platform)} · {profile.Target}";

    static string BuildPlatformName(string? id)
    {
        try
        {
            return BuildPlatforms.Find(id).DisplayName;
        }
        catch (InvalidOperationException)
        {
            return id ?? string.Empty;
        }
    }

    void Details(Gui gui)
    {
        if (profiles.FirstOrDefault(p => p.Id == selected) is not { } profile)
        {
            return;
        }

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(4f).Enter())
        {
            TurianForms.ApplyStyle(gui);
            Title(gui, profile);
            Form(gui);
            Toolchain(gui);
            Actions(gui, profile, FindPlatform(profile.Profile.Profile.Platform));
        }
    }

    void Title(Gui gui, BuildProfileListing profile)
    {
        using (gui.Node(-1, Theme.Scale(26f), "buildProfiles/title").ExpandWidth().Direction(Axis.Horizontal)
                   .Gap(8f).Padding(6f, 2f).AlignContentY(0.5f).Enter())
        {
            gui.DrawText(NameOf(profile), Theme.Text(14f), Theme.Ink, centerInRect: false);
            if (profile.IsActive)
            {
                gui.DrawText("Active", Theme.Text(11f), Theme.Accent, centerInRect: false);
            }
        }
    }

    void Form(Gui gui)
    {
        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(2f).Padding(8f, 2f).Enter())
        {
            gui.ScrollY();
            if (inspection is null)
            {
                return;
            }

            if (gui.Pass == Pass.Pass1Build)
            {
                undo.RecordInspections([inspection], undoLabel);
            }

            for (var s = 0; s < model.Sections.Count; s++)
            {
                var fields = model.Sections[s].BodyFields;
                for (var f = 0; f < fields.Count; f++)
                {
                    var id = $"buildProfiles/{selected}/section{s}/field{f}";
                    if (!PlatformField(gui, fields[f], id))
                    {
                        gui.FormField(fields[f], id, FormContext);
                    }
                }
            }
        }
    }

    internal void Actions(Gui gui, BuildProfileListing profile, IBuildPlatform? platform)
    {
        using (gui.Node(-1, Theme.Scale(Theme.RowHeight + 8f), "buildProfiles/actions").ExpandWidth()
                   .Direction(Axis.Horizontal).Gap(Theme.Gap).Padding(6f, 4f).Enter())
        {
            if (StudioControls.SmallTextButton(gui, "Remove Profile", "buildProfiles/remove", Theme.Scale(110f),
                    "Remove this profile from the project; keep its asset for reuse"))
            {
                pending = () => Remove(profile.Id);
            }

            using (gui.Node().Expand().Enter()) { }

            if (!profile.IsActive
                && StudioControls.SmallTextButton(gui, "Switch Profile", "buildProfiles/switch", Theme.Scale(100f),
                    "Build & Run, exports and play mode follow this profile"))
            {
                pending = () => Switch(profile.Id);
            }

            if (StudioControls.SmallTextButton(gui, "Build", "buildProfiles/build", Theme.Scale(70f),
                    "Export the game with this profile"))
            {
                pending = () => Build(profile.Id);
            }

            if (platform?.Deploys == true
                && StudioControls.SmallTextButton(gui, "Build & Run", "buildProfiles/run", Theme.Scale(100f),
                    "Build a Development player and run it on the selected device"))
            {
                pending = () => Build(profile.Id, install: true);
            }
        }
    }

    static void Line(Gui gui, string text, GuiColor color)
    {
        using (gui.Node(-1, Theme.Scale(rowHeight - 6f), $"buildProfiles/line/{text}").ExpandWidth()
                   .AlignContentY(0.5f).Padding(6f, 0f).Enter())
        {
            gui.DrawText(text, Theme.Text(11f), color, centerInRect: false);
        }
    }

    void Add(AppSettings project, IBuildPlatform platform, BuildTarget target)
    {
        autoSave.Flush(force: true);
        var path = ProjectSettingsFiles.AddBuildProfile(project, platform, target);
        listedFrom = null;
        Refresh(project);
        if (profiles.FirstOrDefault(p => p.Profile.Path == path) is { } added)
        {
            Select(added.Id);
        }
    }

    internal void PopulateAddMenu(FlyoutBuilder menu, AppSettings project, IReadOnlyList<IBuildPlatform> platforms)
    {
        foreach (var platform in platforms)
        {
            foreach (var target in platform.Targets)
            {
                menu.Item($"{platform.DisplayName} {target}", () => pending = () => Add(project, platform, target));
            }
        }
    }

    void Switch(Guid id)
    {
        if (settings.Settings is not { } project)
        {
            return;
        }

        ProjectSettingsFiles.SetActiveBuildProfile(project, id);
        listedFrom = null;
    }

    void Remove(Guid id)
    {
        if (settings.Settings is not { } project)
        {
            return;
        }
        autoSave.Flush(force: true);
        var path = ProjectAsset.PathIn(project.AssetsAbsoluteDir);
        var before = File.ReadAllText(path);
        undo.Perform("Remove Build Profile", [], () =>
        {
            ProjectSettingsFiles.RemoveBuildProfile(project, id);
            listedFrom = null;
        }, () =>
        {
            File.WriteAllText(path, before);
            listedFrom = null;
        }, document: UndoService.ProjectDocument);
        Refresh(project);
    }

    /// <summary>
    /// Saves everything first, so the export reads the profile and the documents as they are shown, and builds nothing
    /// while a document cannot be written.
    /// </summary>
    void Build(Guid id, bool install = false)
    {
        if (settings.Settings is not { } project || !unsaved.SaveBefore("export"))
        {
            return;
        }

        if (BuildProfiles.List(project).FirstOrDefault(p => p.Id == id) is not { } profile)
        {
            return;
        }

        StudioCommands.RunExport(tasks, () => build.ExportAsync(profile.Profile, install));
    }

    FormRenderContext FormContext => formContext ??= CreateFormContext();

    FormRenderContext CreateFormContext()
    {
        referenceRegistration = drawers.Add(ReferenceDrawer.Handles, references);
        return new FormRenderContext
        {
            Drawers = drawers,
            Collapsed = collapsed,
            CanInline = TurianForms.CanInline,
        };
    }

    /// <inheritdoc />
    public void Dispose() => referenceRegistration?.Dispose();
}
