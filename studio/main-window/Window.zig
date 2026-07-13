const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const MenuBar = @import("MenuBar.zig");
const EditorState = @import("../services/EditorState.zig");
const TaskBar = @import("TaskBar.zig");
const Tasks = @import("Tasks.zig");
const PlayMode = @import("../scene-view/PlayMode.zig");
const Documents = @import("Documents.zig");
const ProfilerPanel = @import("ProfilerPanel.zig");
const Panels = @import("Panels.zig");
const ProjectOps = @import("../services/ProjectOps.zig");
const LayoutStore = @import("../services/LayoutStore.zig");
const ReflectJob = @import("../services/ReflectJob.zig");

var should_quit: bool = false;
var hooks_installed: bool = false;
var mouse_left_held: bool = false;
var g_mouse_x: f32 = 0;
var g_mouse_y: f32 = 0;
var g_drag_ghost_rect: gui.Rect = .{ .x = 0, .y = 0, .w = 0, .h = 0 };

/// `closable = false` on a `PanelDesc` guarantees at least one instance of a
/// pinned panel stays open — it pins only the canonical (bare-id) instance,
/// not every extra copy a user opened via Add Panel, which should close
/// like any other added tab.
fn panelInfo(id: []const u8) gui.DockingWidget.PanelInfo {
    const p = Panels.find(id) orelse return .{ .title = id, .closable = true };
    const instance_n = Panels.instanceNumber(id);
    const title = if (instance_n) |n|
        std.fmt.allocPrint(gui.currentWindow().arena(), "{s} ({d})", .{ p.title, n }) catch p.title
    else
        p.title;
    const closable = p.closable or instance_n != null;
    return .{ .title = title, .icon = p.icon, .closable = closable };
}

/// `Dockspace.InitOptions.drawHeaderExtra`: draws into the leaf header's
/// trailing space, which dvui hands us claiming the whole leftover width —
/// see `DockingWidget.zig`'s `tw_expand` note. Used for a
/// right-click-anywhere-in-here "Add Panel" context menu targeting this
/// dock zone, and, right-aligned, the "..." settings button (dvui knows
/// nothing about settings menus, dots icons, or floating menus — that all
/// lives here).
fn panelDrawHeaderExtra(id: []const u8) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .both });
    defer row.deinit();

    const l = LayoutStore.get();
    if (l.findPanel(id)) |leaf| {
        var cxt = gui.context(@src(), .{ .rect = row.data().borderRectScale().r }, .{});
        defer cxt.deinit();
        if (cxt.activePoint()) |cp| {
            var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(cp) }, .{});
            defer fw.deinit();
            if (Panels.drawAddPanelMenuItems(l, leaf)) {
                fw.close();
                LayoutStore.save(gui.io);
            }
        }
    }

    _ = gui.spacer(@src(), .{ .expand = .horizontal });

    const p = Panels.find(id) orelse return;
    const settings_fn = p.settings orelse return;
    var m = gui.menu(@src(), .horizontal, .{ .gravity_y = 0.5 });
    defer m.deinit();
    if (gui.menuItemIcon(@src(), "docktab_settings", gui.entypo.dots_three_vertical, .{ .submenu = true }, .{
        .gravity_y = 0.5,
        .padding = gui.Rect.all(2),
        .margin = gui.Rect.all(2),
    })) |r| {
        var fw = gui.floatingMenu(@src(), .{ .from = r }, .{});
        defer fw.deinit();
        settings_fn(id);
    }
}

/// `Dockspace.InitOptions.onTabContextMenu`: right-clicking an existing tab
/// (as opposed to the empty header space `panelDrawHeaderExtra` already
/// handles) opens the same Add-Panel list, targeting that tab's leaf.
fn panelTabContextMenu(id: []const u8, pt: gui.Point.Natural) void {
    const l = LayoutStore.get();
    const leaf = l.findPanel(id) orelse return;

    var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(pt) }, .{});
    defer fw.deinit();
    if (Panels.drawAddPanelMenuItems(l, leaf)) {
        fw.close();
        LayoutStore.save(gui.io);
    }
}

/// Wired to `ReflectJob.onRescan` on the first frame: re-syncs the
/// custom-panel registry after a user-code discovery scan. See
/// `Panels.zig`'s module doc for why nothing populates it yet.
fn rescanCustomPanels() void {
    Panels.registerCustom(&.{});
}

/// Draw one frame of the editor UI. Returns true to continue, false to quit.
pub fn frame() bool {
    if (!hooks_installed) {
        hooks_installed = true;
        ReflectJob.onRescan = rescanCustomPanels;
    }

    // Recording is tied to Play and controlled from the panel (Record/Pause +
    // auto-on-Play). `tickRecording` arms `engine.Profiler.enabled` for this
    // frame. When disabled, begin/end and all zones/counters early-out.
    ProfilerPanel.tickRecording();
    engine.Profiler.beginFrame();
    defer engine.Profiler.endFrame();

    for (gui.events()) |*e| {
        if (e.evt == .window and e.evt.window.action == .close) return false;
        if (e.evt == .app and e.evt.app.action == .quit) return false;
        switch (e.evt) {
            .mouse => |me| {
                if (me.action == .position or me.action == .press or me.action == .release) {
                    const scale = gui.windowNaturalScale();
                    g_mouse_x = me.p.x / scale;
                    g_mouse_y = me.p.y / scale;
                }
                if (me.button == .left) {
                    if (me.action == .press) mouse_left_held = true;
                    if (me.action == .release) mouse_left_held = false;
                }
            },
            else => {},
        }
    }
    defer EditorState.endFrameDrag(mouse_left_held);

    if (should_quit) return false;

    var root = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer root.deinit();

    // Times the editor's CPU build + viewport render work for the timeline.
    var ui_zone = engine.Profiler.zone("studio.ui");
    defer ui_zone.end();

    // Handle global keyboard shortcuts after root is created
    for (gui.events()) |*e| {
        if (e.evt != .key) continue;
        const ke = e.evt.key;
        if (ke.action != .down or !ke.mod.control()) continue;

        if (ke.code == .z and !ke.mod.shift()) {
            e.handle(@src(), root.data());
            EditorState.undo();
        } else if (ke.code == .z and ke.mod.shift()) {
            e.handle(@src(), root.data());
            EditorState.redo();
        } else if (ke.code == .y) {
            e.handle(@src(), root.data());
            EditorState.redo();
        } else if (ke.code == .c and !ke.mod.shift()) {
            if (EditorState.selectedCount() > 0) {
                e.handle(@src(), root.data());
                EditorState.copySelectedObjects();
            }
        } else if (ke.code == .x and !ke.mod.shift()) {
            if (EditorState.selectedCount() > 0) {
                e.handle(@src(), root.data());
                EditorState.copySelectedObjects();
                EditorState.deleteSelectedObjects(gui.frameTimeNS());
            }
        } else if (ke.code == .v and !ke.mod.shift()) {
            if (EditorState.hasClipboard()) {
                e.handle(@src(), root.data());
                EditorState.pasteObjects(gui.frameTimeNS(), gui.io);
            }
        } else if (ke.code == .p and !ke.mod.shift()) {
            // Ctrl+P toggles Play / Stop.
            e.handle(@src(), root.data());
            PlayMode.toggle(gui.io);
        } else if (ke.code == .s and !ke.mod.shift()) {
            // Ctrl+S saves the active scene (replaces the old File ▸ Save Scene item).
            if (EditorState.current_scene_path) |path| {
                e.handle(@src(), root.data());
                ProjectOps.saveScene(path);
            }
        }
    }

    MenuBar.draw(&should_quit);

    _ = gui.separator(@src(), .{ .expand = .horizontal });

    // Document tab strip. Drawn above the editing surface.
    Documents.drawTabBar(mouse_left_held);

    // Main editor area. Scoped in a block so the dockspace is deinit'd
    // (popped from dvui's layout stack) *before* the bottom task bar is drawn;
    // otherwise the task bar would nest inside the still-open dockspace.
    {
        var dock = gui.dockspace(@src(), .{
            .layout = LayoutStore.get(),
            .panelInfo = panelInfo,
            .close_button_visibility = .hover,
            .drawHeaderExtra = panelDrawHeaderExtra,
            .onTabContextMenu = panelTabContextMenu,
        }, .{ .expand = .both });
        defer dock.deinit();
        while (dock.panel()) |p| {
            defer p.end();
            Panels.drawById(p.id);
        }
        if (dock.changed) LayoutStore.save(gui.io);
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal });
    TaskBar.draw();

    drawDragGhost();

    // Reap finished background jobs and keep frames flowing while one runs.
    Tasks.pump(gui.io);
    EditorState.pumpReflect(gui.io);

    // Step the in-editor game simulation. Keeps frames flowing
    // while a scene is playing so the viewport animates continuously.
    PlayMode.pump(gui.io);

    return true;
}

/// Small floating label that follows the cursor during asset / scene-node drags,
/// showing an icon and the name of the item being dragged.
fn drawDragGhost() void {
    if (EditorState.drag_kind == .none) return;

    gui.cursorSet(.arrow_all);

    g_drag_ghost_rect.x = g_mouse_x + 12;
    g_drag_ghost_rect.y = g_mouse_y + 12;

    var fw = gui.floatingWindow(@src(), .{
        .rect = &g_drag_ghost_rect,
        .resize = .none,
        .stay_above_parent_window = true,
        .window_avoid = .none,
    }, .{
        .background = true,
        .style = .window,
        .border = .all(1),
        .corners = .all(4),
        .padding = .all(4),
    });
    defer fw.deinit();

    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .background = false });
    defer row.deinit();

    switch (EditorState.drag_kind) {
        .asset => {
            const path = EditorState.dragAssetPath();
            const name = if (@import("std").mem.lastIndexOfScalar(u8, path, '/')) |sep|
                path[sep + 1 ..]
            else
                path;
            const asset_type = editor.asset_registry.lookupByFilename(name);
            const desc = editor.asset_registry.get(asset_type);
            const icon_bytes = switch (desc.icon_hint) {
                .document => gui.entypo.text_document,
                .code => gui.entypo.code,
                .image => gui.entypo.image,
                .sound => gui.entypo.sound,
                .model => gui.entypo.layers,
                .material => gui.entypo.colours,
                .data => gui.entypo.database,
                .font => gui.entypo.text,
            };
            gui.icon(@src(), "di", icon_bytes, .{}, .{
                .min_size_content = .{ .w = 14, .h = 14 },
                .gravity_y = 0.5,
                .padding = .{ .w = 4 },
            });
            gui.label(@src(), "{s}", .{name}, .{ .gravity_y = 0.5 });
        },
        .game_object => {
            const idx = EditorState.drag_object_idx;
            const name = if (idx < EditorState.object_count)
                EditorState.objects[idx].nameSlice()
            else
                "Object";
            gui.icon(@src(), "di", gui.entypo.layers, .{}, .{
                .min_size_content = .{ .w = 14, .h = 14 },
                .gravity_y = 0.5,
                .padding = .{ .w = 4 },
            });
            gui.label(@src(), "{s}", .{name}, .{ .gravity_y = 0.5 });
        },
        .none => {},
    }
}
