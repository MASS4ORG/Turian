using System.Text.Json;

namespace Gaya.Plugin.Turian;

/// <summary>
/// Shows the Bricks panel's selection in the Turian inspector: the selected brick or registry, and the project's
/// brick settings, as forms with their actions.
/// </summary>
/// <param name="inspector">The inspector the forms are selected into.</param>
/// <param name="settings">The open project's settings, whose brick settings the settings form edits.</param>
sealed class TurianBrickInspector(NodeInspectorController inspector, SettingsService settings) : IBrickInspector
{
    /// <inheritdoc />
    public void ShowSelection(BricksController controller) => inspector.Select(BrickInspections.Selection(controller));

    /// <inheritdoc />
    public void ShowSettings(BricksController controller) => inspector.Select(InspectSettings(controller));

    /// <inheritdoc />
    public void Refresh(BricksController controller)
    {
        // A registry being edited keeps its unsaved values until it is saved, removed or another is selected.
        if (inspector.SelectedObject is not FormInspection inspection)
        {
            return;
        }

        if (IsCurrentRegistry(controller, inspection))
        {
            return;
        }

        if (inspection.Key == "bricks-settings")
        {
            if (!Equals(inspection.Context, controller.WorkspaceRoot))
            {
                inspector.Select(InspectSettings(controller));
            }

            return;
        }

        inspector.Select(BrickInspections.Selection(controller));
    }

    /// <summary>The open project's declared brick settings, with save and reload actions; none for studio bricks.</summary>
    /// <param name="controller">The panel's controller.</param>
    /// <returns>The form, or <c>null</c>.</returns>
    public FormInspection? InspectSettings(BricksController controller) =>
        controller.Scope == BrickScope.Project && settings.Settings is { } project
            ? BrickInspections.Settings(controller, project.Bricks)
            : null;

    static bool IsCurrentRegistry(BricksController controller, FormInspection inspection) =>
        Equals(inspection.Context, controller.WorkspaceRoot)
        && inspection.Target is ScopedRegistry
        && inspection.Key == $"registry:{controller.SelectedRegistry}"
        && controller.Tab == BricksTab.Registries;
}

/// <summary>Composes manifest forms with the actions and calculated values of the active workspace.</summary>
static class BrickInspections
{
    /// <summary>Builds an inspection for the controller's selected brick or registry.</summary>
    public static FormInspection? Selection(BricksController controller)
    {
        if (controller.Tab == BricksTab.Registries)
        {
            return Registry(controller);
        }

        var brick = controller.Catalog.FirstOrDefault(b => b.Id == controller.Selected);
        return brick is null ? null : Brick(controller, brick, controller.Installed.FirstOrDefault(p => p.Id == brick.Id));
    }

    /// <summary>
    /// Builds a brick's form: its status, version and actions first, then its versions, forms and downloads, then the
    /// details a user chooses by, such as its description and dependencies. Packaging internals, such as excluded
    /// files and bake settings, stay in its manifest. Enabling, disabling and version changes are staged for the
    /// panel's Apply.
    /// </summary>
    public static FormInspection Brick(BricksController controller, CatalogBrick brick, ResolvedPackage? resolved)
    {
        var manifest = Manifest(brick, resolved);
        var model = InspectorForms.Build(manifest, readOnly: true);
        var pending = controller.PendingFor(brick.Id);
        var summary = new FormSection($"Brick '{brick.DisplayName ?? brick.Id}'", manifest,
        [
            FormField.Display("Status", manifest, () => Status(brick, resolved)),
            FormField.Display("Version", manifest, () => Version(brick, manifest)),
            FormField.Display("Id", manifest, () => brick.Id),
            .. Text("Installs In", manifest, InstallsIn(controller, brick)),
            .. Text("Pending", manifest, pending is null ? null : PendingText(pending)),
            .. Text("Also Disables", manifest, string.Join(", ", controller.Pending.Where(c => c.Because == brick.Id).Select(static c => c.Id))),
            .. Text("Linked To", manifest, brick.Link),
        ])
        { Buttons = pending is null ? Actions(controller, brick, resolved) : PendingActions(controller, brick) };
        var details = new FormSection("Details", manifest,
        [
            .. Text("Description", manifest, manifest.Description),
            .. Text("Author", manifest, manifest.Author),
            .. Text("License", manifest, manifest.License),
            .. Text("Keywords", manifest, string.Join(", ", manifest.Keywords)),
            .. Text("Dependencies", manifest, string.Join(", ", manifest.Dependencies.Keys)),
            .. Text("Needed By", manifest, string.Join(", ", controller.RequiredBy(brick.Id))),
            .. Text("Origin", manifest, brick.Origin),
            .. Text("Installed At", manifest, resolved?.RootPath),
        ]);
        FormSection[] sections = Versions(controller, brick, manifest) is { } versions ? [summary, versions, details] : [summary, details];
        return new FormInspection(manifest, model with { Sections = sections }, $"brick:{brick.Id}", controller.WorkspaceRoot);
    }

    // The staged version transition, with the brick it follows from when it was not chosen directly.
    static string PendingText(BrickChange change) =>
        change.Because is null ? change.Transition : $"{change.Transition}, because it needs {change.Because}";

    /// <summary>The title of the registry form's section of signing keys, which starts folded.</summary>
    public const string SigningTitle = "Signing Keys";

    /// <summary>
    /// The versions a registry lists, to use any one of them; the form in use and the forms published; and the copies
    /// on this machine, with the old ones removable. Null when there is nothing to show.
    /// </summary>
    static FormSection? Versions(BricksController controller, CatalogBrick brick, PackageManifest manifest)
    {
        var choice = brick.Versions.Count > 0 ? new BrickVersionChoice(brick) : null;
        var unused = controller.UnusedDownloads(brick);
        var forms = PublishedForms(brick).ToList();
        var used = brick.UsedVariant is { } variant && (variant != PackageVariant.Source || forms.Count > 0) ? FormName(variant) : null;
        FormField[] fields =
        [
            .. choice is null ? [] : (FormField[])[FormField.Display("Available", manifest, () => choice)],
            .. Text("Form", manifest, used),
            .. Text("Forms Published", manifest, string.Join(", ", forms.Select(FormName))),
            .. Text("Downloads", manifest, Downloads(brick)),
        ];
        if (fields.Length == 0)
        {
            return null;
        }

        List<Button> buttons = [];
        if (choice is not null)
        {
            buttons.Add(Staged(controller, "Use Selected Version", () => controller.StageVersion(brick, choice.Picked!),
                () => choice.Picked is { } picked && !(brick.IsInstalled && picked == brick.InstalledVersion)));
        }

        if (unused.Count > 0)
        {
            buttons.Add(Action(controller, $"Remove {unused.Count} Old Download{(unused.Count == 1 ? "" : "s")}",
                () => controller.RemoveUnusedDownloadsAsync(brick)));
        }

        return new FormSection("Versions", manifest, fields) { Buttons = buttons };
    }

    // The forms of the version in use, or of the newest, that the registry publishes or this machine holds.
    static IEnumerable<string> PublishedForms(CatalogBrick brick)
    {
        var version = brick.IsInstalled ? brick.InstalledVersion : brick.LatestVersion;
        var listed = brick.Versions.FirstOrDefault(v => v.Version == version)?.Variants ?? [];
        var stored = brick.Downloads.Where(d => d.Version == version).Select(static d => d.Variant);
        var forms = listed.Concat(stored).Distinct(StringComparer.Ordinal)
            .OrderBy(static f => f != PackageVariant.Source).ThenBy(static f => f, StringComparer.Ordinal).ToList();
        return forms.Count > 1 || forms.Any(static f => f != PackageVariant.Source) ? forms : [];
    }

    static string FormName(string variant) => variant switch
    {
        PackageVariant.Source => "Source",
        "bake" => "Baked twin",
        _ => $"Baked for {variant}",
    };

    static string? Downloads(CatalogBrick brick)
    {
        if (brick.Downloads.Count == 0)
        {
            return null;
        }

        var versions = brick.Downloads.Select(static d => d.Version).Distinct(StringComparer.Ordinal)
            .OrderByDescending(static v => SemanticVersion.TryParse(v, out var parsed) ? parsed : null).ToList();
        var size = BricksController.FormatSize(brick.Downloads.Sum(BricksController.SizeOf));
        return $"{brick.Downloads.Count} on this machine ({size}): {string.Join(", ", versions)}";
    }

    // Where a brick of the other scope goes when it is enabled from here.
    static string? InstallsIn(BricksController controller, CatalogBrick brick) =>
        controller.Workspace is { } workspace && !brick.Accepts(workspace.Scope)
            ? controller.ScopeFor(brick) == BrickScope.Studio ? "Studio, for every project" : "The open project"
            : null;

    // A detail row only when there is something to show.
    static IEnumerable<FormField> Text(string label, PackageManifest manifest, string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [FormField.Display(label, manifest, () => value)];

    // The version in use or on offer, and the newer one when there is an update.
    static string Version(CatalogBrick brick, PackageManifest manifest)
    {
        var current = brick.InstalledVersion ?? manifest.Version?.ToString() ?? brick.LatestVersion ?? "";
        return brick.HasUpdate ? $"{current} ({brick.LatestVersion} available)" : current;
    }

    static PackageManifest Manifest(CatalogBrick brick, ResolvedPackage? resolved) =>
        resolved?.Manifest ?? brick.Manifest ?? new PackageManifest
        {
            Name = brick.Id,
            DisplayName = brick.DisplayName,
            Description = brick.Description,
            Author = brick.Author,
            Version = SemanticVersion.TryParse(brick.LatestVersion ?? "", out var version) ? version : null,
        };
    static string Status(CatalogBrick brick, ResolvedPackage? resolved) => brick.State switch
    {
        BrickState.Enabled when resolved?.Origin == PackageOrigin.Embedded => "In use, local to the project",
        BrickState.Enabled => "In use",
        BrickState.Installed => "Downloaded, not in use",
        _ => "Available",
    };

    static IReadOnlyList<Button> Actions(BricksController controller, CatalogBrick brick, ResolvedPackage? resolved) =>
    [
        .. FormActions(controller, brick, resolved),
        .. StandardActions(controller, brick, resolved),
        .. brick.Link is null ? [] : (Button[])[Action(controller, "Remove Link", () => controller.UnlinkAsync(brick.Id))],
    ];

    static IReadOnlyList<Button> FormActions(BricksController controller, CatalogBrick brick, ResolvedPackage? resolved)
    {
        if (controller.Workspace is not IBrickTwinWorkspace || brick.State != BrickState.Enabled)
        {
            return [];
        }

        return resolved switch
        {
            { Origin: PackageOrigin.Registry, Manifest.BakeOf: not null } =>
                [Action(controller, "Edit Source", () => controller.SwapAsync(brick.Id, toSource: true))],
            { Origin: PackageOrigin.Embedded, Manifest: { Upstream: not null, AllowsTwins: true } } =>
                [Action(controller, "Use Baked Twin", () => controller.SwapAsync(brick.Id, toSource: false))],
            _ => [],
        };
    }

    static IReadOnlyList<Button> StandardActions(BricksController controller, CatalogBrick brick,
        ResolvedPackage? resolved) => brick.State switch
        {
            BrickState.Available => [Staged(controller, "Enable", () => controller.Toggle(brick))],
            BrickState.Installed =>
            [
                Staged(controller, "Enable", () => controller.Toggle(brick)),
                Action(controller, "Uninstall", () => controller.UninstallAsync(brick.Id)),
            ],
            _ when resolved?.Origin == PackageOrigin.Embedded =>
            [
                Action(controller, "Revert To Global", () => controller.RevertAsync(brick.Id)),
                Action(controller, "Uninstall", () => controller.UninstallAsync(brick.Id)),
            ],
            _ when controller.Workspace?.SupportsForks != true || controller.ScopeFor(brick) != controller.Scope =>
            [
                Staged(controller, "Disable", () => controller.Toggle(brick)),
                Staged(controller, "Update", () => controller.StageUpdate(brick)),
            ],
            _ =>
            [
                Staged(controller, "Disable", () => controller.Toggle(brick)),
                Staged(controller, "Update", () => controller.StageUpdate(brick)),
                Action(controller, "Make Local", () => controller.EmbedAsync(brick.Id)),
            ],
        };

    // A brick with a change waiting offers to apply every waiting change, or to drop its own.
    static IReadOnlyList<Button> PendingActions(BricksController controller, CatalogBrick brick) =>
    [
        Action(controller, controller.Pending.Count == 1 ? "Apply Change" : $"Apply {controller.Pending.Count} Changes",
            controller.ApplyPendingAsync),
        Staged(controller, "Undo Change", () => controller.Unstage(brick.Id)),
    ];

    static FormInspection? Registry(BricksController controller)
    {
        if (controller.SelectedRegistry is not { } name)
        {
            return null;
        }

        var registry = name.Length == 0 ? new ScopedRegistry() : controller.Registries.FirstOrDefault(r => r.Name == name);
        if (registry is null)
        {
            return null;
        }

        var readOnly = name == controller.Workspace?.PublicRegistry?.Name;
        var draft = JsonSerializer.Deserialize<ScopedRegistry>(JsonSerializer.Serialize(registry, PackageJson.Options),
            PackageJson.Options)!;
        var model = InspectorForms.Build(draft, readOnly: readOnly);
        var savedName = name.Length == 0 ? null : name;
        var title = savedName is null ? "New registry" : $"Registry '{savedName}'";
        var fields = model.Sections[0].Fields;
        static bool IsSigning(FormField field) => field.Name is nameof(ScopedRegistry.Keys) or nameof(ScopedRegistry.AllowUnsigned);
        var section = new FormSection(title, draft, [.. fields.Where(field => !IsSigning(field))])
        {
            Buttons = readOnly ? [] :
            [
                Action(controller, "Save", () => controller.SaveRegistryAsync(savedName, draft)),
                .. savedName is null ? [] : (Button[])[Action(controller, "Remove", () => controller.RemoveRegistryAsync(savedName))],
            ],
        };

        // The keys are for people who run their own registry; they start folded under their own heading.
        var signing = new FormSection(SigningTitle, draft, [.. fields.Where(IsSigning)]);
        return new FormInspection(draft, model with { Sections = [section, signing] }, $"registry:{name}", controller.WorkspaceRoot);
    }

    /// <summary>Builds the project's brick settings form with explicit persistence actions.</summary>
    public static FormInspection Settings(BricksController controller, BricksSettings settings)
    {
        var model = InspectorForms.Build(settings);
        var project = controller.WorkspaceRoot;
        bool CanReload() => project is not null && controller.WorkspaceRoot == project && !controller.IsBusy;
        var section = new FormSection("Bricks Settings", settings, model.Sections[0].Fields)
        {
            Buttons =
            [
                Action(controller, "Save", () => controller.SaveSettingsAsync(settings)),
                new Button("Reload", () => { if (CanReload()) { settings.Reload(); } }, CanReload),
            ],
        };
        return new FormInspection(settings, model with { Sections = [section] }, "bricks-settings", controller.WorkspaceRoot);
    }

    static Button Action(BricksController controller, string label, Func<Task<bool>> action)
    {
        var project = controller.WorkspaceRoot;
        bool Enabled() => project is not null && controller.WorkspaceRoot == project && !controller.IsBusy;
        return new Button(label, () => { if (Enabled()) { _ = action(); } }, Enabled);
    }

    // A button that stages or drops a change for the panel's Apply rather than running it.
    static Button Staged(BricksController controller, string label, Action stage, Func<bool>? allowed = null)
    {
        var project = controller.WorkspaceRoot;
        bool Enabled() => project is not null && controller.WorkspaceRoot == project && !controller.IsBusy
                           && (allowed?.Invoke() ?? true);
        return new Button(label, () => { if (Enabled()) { stage(); } }, Enabled);
    }
}
