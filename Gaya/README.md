# Gaya

Gaya is a plugin-driven desktop workbench built on [Guinevere](https://github.com/MASS4ORG/Guinevere).
The shell knows nothing about what it is editing: panels, commands, menus, key bindings and chrome all
arrive from plugins, so the same host can be a game studio, a code editor or a 3D modeller depending on
which plugins are loaded.

| Package | Referenced by |
| --- | --- |
| `MASS4.Gaya.Sdk` | plugins — the contract, and the only Gaya assembly a plugin needs |
| `MASS4.Gaya.Host` | the application shell — plugin activation, workbench, docking, layout persistence |

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

`Gaya.Plugin.GameStudio` in the [Turian](https://github.com/MASS4ORG/Turian) repository is the reference
implementation. The full contract — lifecycle, every registry, versioning policy and the boundary rules —
is documented in [`docs/decisions/Gaya-Platform.md`](https://github.com/MASS4ORG/Turian/blob/main/docs/decisions/Gaya-Platform.md).

Gaya lives in the Turian repository for now and is versioned independently of it. It does not reference
Turian in any direction; that boundary is enforced by a test.
