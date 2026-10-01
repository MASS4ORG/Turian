using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>Composes manifest forms with the actions and calculated values of the active project.</summary>
public static class BrickInspections
{
    /// <summary>Builds an inspection for the controller's selected brick or registry.</summary>
    public static FormInspection? Selection(BricksController controller, IReadOnlyList<ResolvedPackage> installed)
    {
        if (controller.Tab == BricksTab.Registries) return Registry(controller);
        var brick = controller.Catalog.FirstOrDefault(b => b.Id == controller.Selected);
        return brick is null ? null : Brick(controller, brick, installed.FirstOrDefault(p => p.Id == brick.Id));
    }

    /// <summary>Builds a manifest form without inventing missing metadata for registry-only entries.</summary>
    public static FormInspection Brick(BricksController controller, CatalogBrick brick, ResolvedPackage? resolved)
    {
        var manifest = Manifest(brick, resolved);
        var model = InspectorForms.Build(manifest, readOnly: true);
        var fields = model.Sections[0].Fields;
        if (resolved is null && brick.Manifest is null)
            fields = [.. fields.Where(f => f.Name is "Name" or "DisplayName" or "Description" or "Author" or "Version")];
        var metadata = new FormSection($"Brick '{brick.DisplayName ?? brick.Id}'", manifest, fields);
        var installation = new FormSection("Installation", manifest,
        [
            FormField.Display("Latest", manifest, () => brick.LatestVersion ?? ""),
            FormField.Display("Status", manifest, () => Status(brick, resolved)),
            FormField.Display("Origin", manifest, () => brick.Origin ?? ""),
            FormField.Display("Installed At", manifest, () => resolved?.RootPath ?? ""),
            FormField.Display("Needed By", manifest, () => controller.RequiredBy(brick.Id)),
        ])
        { Buttons = Actions(controller, brick, resolved) };
        return new FormInspection(manifest, model with { Sections = [metadata, installation] },
            $"brick:{brick.Id}", controller.ProjectFolder);
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
        BrickState.Installed => "On this machine, not in use",
        _ => "Available",
    };

    static IReadOnlyList<Button> Actions(BricksController controller, CatalogBrick brick,
        ResolvedPackage? resolved) => brick.State switch
        {
            BrickState.Available => [Action(controller, "Enable", () => controller.EnableAsync(brick))],
            BrickState.Installed =>
            [
                Action(controller, "Enable", () => controller.EnableAsync(brick)),
            Action(controller, "Uninstall", () => controller.UninstallAsync(brick.Id)),
        ],
            _ when resolved?.Origin == PackageOrigin.Embedded =>
            [
                Action(controller, "Revert To Global", () => controller.RevertAsync(brick.Id)),
            Action(controller, "Uninstall", () => controller.UninstallAsync(brick.Id)),
        ],
            _ =>
            [
                Action(controller, "Disable", () => controller.DisableAsync(brick.Id)),
            Action(controller, "Update", () => controller.UpdateAsync(brick.Id)),
            Action(controller, "Make Local", () => controller.EmbedAsync(brick.Id)),
        ],
        };

    static FormInspection? Registry(BricksController controller)
    {
        if (controller.SelectedRegistry is not { } name) return null;
        var registry = name.Length == 0 ? new ScopedRegistry() : controller.Registries.FirstOrDefault(r => r.Name == name);
        if (registry is null) return null;
        var readOnly = name == ProjectPackages.PublicRegistry.Name;
        var draft = JsonSerializer.Deserialize<ScopedRegistry>(JsonSerializer.Serialize(registry, PackageJson.Options),
            PackageJson.Options)!;
        var model = InspectorForms.Build(draft, readOnly: readOnly);
        var savedName = name.Length == 0 ? null : name;
        var title = savedName is null ? "New registry" : $"Registry '{savedName}'";
        var section = new FormSection(title, draft, model.Sections[0].Fields)
        {
            Buttons = readOnly ? [] :
            [
                Action(controller, "Save", () => controller.SaveRegistryAsync(savedName, draft)),
                Action(controller, "Remove", () => controller.RemoveRegistryAsync(savedName ?? draft.Name)),
            ],
        };
        return new FormInspection(draft, model with { Sections = [section] }, $"registry:{name}", controller.ProjectFolder);
    }

    /// <summary>Builds the project's brick settings form with explicit persistence actions.</summary>
    public static FormInspection Settings(BricksController controller, BricksSettings settings)
    {
        var model = InspectorForms.Build(settings);
        var project = controller.ProjectFolder;
        bool CanReload() => project is not null && controller.ProjectFolder == project && !controller.IsBusy;
        var section = new FormSection("Bricks Settings", settings, model.Sections[0].Fields)
        {
            Buttons =
            [
                Action(controller, "Save", () => controller.SaveSettingsAsync(settings)),
                new Button("Reload", () => { if (CanReload()) settings.Reload(); }, CanReload),
            ],
        };
        return new FormInspection(settings, model with { Sections = [section] }, "bricks-settings", controller.ProjectFolder);
    }

    static Button Action(BricksController controller, string label, Func<Task<bool>> action)
    {
        var project = controller.ProjectFolder;
        bool Enabled() => project is not null && controller.ProjectFolder == project && !controller.IsBusy;
        return new Button(label, () => { if (Enabled()) _ = action(); }, Enabled);
    }
}
