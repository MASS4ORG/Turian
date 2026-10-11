# Gaya

Gaya is a plugin-driven desktop workbench built on [Guinevere](https://github.com/MASS4ORG/Guinevere).
The shell knows nothing about what it is editing: panels, commands, menus, key bindings and chrome all
arrive from plugins, so the same host can be a game studio, a code editor or a 3D modeller depending on
which plugins are loaded.

| Package | Referenced by |
| --- | --- |
| `MASS4.Gaya.Attributes` | settings metadata without a UI dependency; included transitively by the SDK |
| `MASS4.Gaya.Sdk` | plugins — the contract, and the only Gaya assembly a plugin needs |
| `MASS4.Gaya.Host` | the application shell — plugin activation, workbench, docking, layout persistence |
| `Gaya.Packages` | hosts — brick manifests, sources, resolution, the shared store and registries |

A plugin is a class carrying `[Plugin(id, displayName)]` and implementing `IPlugin`:

```csharp
[Plugin("com.example.notes", "Notes")]
public sealed class NotesPlugin : IPlugin
{
    public void Configure(IPluginContext ctx)
    {
        ctx.Services.AddSingleton<NoteStore>();
        ctx.Panels.Register(new PanelDescriptor("notes.list", "Notes", PanelPlacement.Right,
            services => new NotesPanel(services.GetRequiredService<NoteStore>())));
        ctx.Commands.Register(new CommandDescriptor("notes.new", "Notes: New Note",
            services => services.GetRequiredService<NoteStore>().Add()));
        ctx.Menus.Add(new MenuItemDescriptor(MenuIds.File, "notes.new"));
    }
}
```

`Gaya.Plugin.Turian` in the [Turian](https://github.com/MASS4ORG/Turian) repository is the reference
implementation. The full contract — lifecycle, every registry, versioning policy and the boundary rules —
is documented in [`docs/decisions/Gaya-Platform.md`](https://github.com/MASS4ORG/Turian/blob/main/docs/decisions/Gaya-Platform.md).

Gaya lives in the Turian repository for now and ships on Turian's version (also for now). It does not reference
Turian in any direction; that boundary is enforced by a test.

## Settings

`Gaya.EditorSettingAttribute` is the sole configuration source for built-in and user-defined settings pages. The class declares its category, stable storage id, description, order, workspace scope and hidden state; member attributes declare option labels and descriptions. Registration supplies the live instance and restores its persisted values.

```csharp
[Gaya.EditorSetting("General/Performance", Id = "example.performance")]
public sealed class PerformanceSettings
{
    [Gaya.EditorSetting("Cap FPS")]
    public int CapFps { get; set; } = 60;
}

// During plugin configuration:
var preferences = new PerformanceSettings();
ctx.Services.AddSingleton(preferences);
ctx.Settings.Register(preferences);
```

An omitted `Id` derives the storage key from the type's full name. Keep an explicit id stable when renaming a settings type; changing its category does not change that key. `Workspace = true` stores values with the project, and `Hidden = true` persists a page without listing it in Settings. Turian discovers annotated user classes with public parameterless constructors after compilation and registers them through this same API.

## Panel headers

A panel implements `Render` for its body and optionally `RenderHeader` for widgets in its dock group's remaining tab-strip space. Gaya invokes both on the same panel instance in both Guinevere passes. The header can contain menus, lock buttons, multiple controls or custom widgets; ordinary child layout uses the available area automatically.

```csharp
public sealed class NotesPanel : IPanel
{
    public void Render(Gui gui) => gui.DrawText("Notes");

    public void RenderHeader(Gui gui, PanelHeaderContext context)
    {
        using (gui.Node().Expand().Direction(Axis.Horizontal).Enter())
            gui.DrawText("Header widgets");
    }
}
```

`PanelHeaderContext` supplies the panel instance id, translated title, available screen-space rectangle, focus state and host services. The build pass's rectangle comes from the preceding layout; use normal layout nodes for measuring widgets and handle input only during `Pass2Render`. Header clicks focus the panel for scoped shortcuts. Inactive tabs do not render their headers, and panel disposal also releases header-owned state. An annotated Turian user panel implementing `IPanel` uses these rendering methods; reference Gaya.Sdk and Guinevere from an editor-only assembly for custom rendering. Plain annotated objects continue to use reflected forms.

## API migration

- Replace `MASS4.Attributes.EditorSettingAttribute` with `Gaya.EditorSettingAttribute` from `MASS4.Gaya.Attributes`. Use `[Gaya.EditorSetting(...)]` when importing both namespaces; regenerated Turian user projects provide an alias for `[EditorSetting]`.
- Move settings page ids, paths, descriptions, order, scope and visibility into the class attribute. Replace `settings.Register(new SettingsPageDescriptor(...))` with `settings.Register(instance)`; descriptors expose read-only metadata derived from that instance. Preserve each existing id to retain preferences.
- Move `TabStripChromeDescriptor` contributions into the panel's `RenderHeader` method. The tab-strip registry and `IPluginContext.TabStripChrome` have been removed. Keep application-wide chrome contributions on `IPluginContext.Chrome`.
- Recompile plugins and user scripts against the updated Gaya assemblies. These changes require a major release under the release policy.
