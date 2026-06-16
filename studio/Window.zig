const dvui = @import("dvui");
const editor = @import("editor");
const MenuBar = @import("MenuBar.zig");
const SceneTree = @import("SceneTree.zig");
const Inspector = @import("Inspector.zig");
const AssetBrowser = @import("AssetBrowser.zig");
const SceneViewport = @import("SceneViewport.zig");
const EditorState = @import("EditorState.zig");
const TaskBar = @import("TaskBar.zig");
const Tasks = @import("Tasks.zig");
const PlayMode = @import("PlayMode.zig");

var should_quit: bool = false;
var mouse_left_held: bool = false;
var g_inspector_ratio: f32 = 0.7;
var g_mouse_x: f32 = 0;
var g_mouse_y: f32 = 0;
var g_drag_ghost_rect: dvui.Rect = .{ .x = 0, .y = 0, .w = 0, .h = 0 };

/// Draw one frame of the editor UI. Returns true to continue, false to quit.
pub fn frame() bool {
    for (dvui.events()) |*e| {
        if (e.evt == .window and e.evt.window.action == .close) return false;
        if (e.evt == .app and e.evt.app.action == .quit) return false;
        switch (e.evt) {
            .mouse => |me| {
                if (me.action == .position or me.action == .press or me.action == .release) {
                    const scale = dvui.windowNaturalScale();
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

    var root = dvui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer root.deinit();

    // Handle global keyboard shortcuts after root is created
    for (dvui.events()) |*e| {
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
                EditorState.deleteSelectedObjects(dvui.frameTimeNS());
            }
        } else if (ke.code == .v and !ke.mod.shift()) {
            if (EditorState.hasClipboard()) {
                e.handle(@src(), root.data());
                EditorState.pasteObjects(dvui.frameTimeNS(), dvui.io);
            }
        } else if (ke.code == .p and !ke.mod.shift()) {
            // Ctrl+P toggles Play / Stop (issue #31).
            e.handle(@src(), root.data());
            PlayMode.toggle(dvui.io);
        }
    }

    MenuBar.draw(&should_quit);

    _ = dvui.separator(@src(), .{ .expand = .horizontal });

    // Main editor area. Scoped in a block so the paned widgets are deinit'd
    // (popped from dvui's layout stack) *before* the bottom task bar is drawn;
    // otherwise the task bar would nest inside the still-open paned.
    {
        var split_h = dvui.paned(@src(), .{
            .direction = .horizontal,
            .collapsed_size = 0,
            .handle_margin = 4,
            .split_ratio = &g_inspector_ratio,
        }, .{ .expand = .both });
        defer split_h.deinit();
        if (split_h.showFirst()) {
            var split_v = dvui.paned(@src(), .{
                .direction = .vertical,
                .collapsed_size = 0,
                .handle_margin = 4,
            }, .{ .expand = .both });
            defer split_v.deinit();

            if (split_v.showFirst()) {
                var split_h2 = dvui.paned(@src(), .{
                    .direction = .horizontal,
                    .collapsed_size = 0,
                    .handle_margin = 4,
                }, .{ .expand = .both });
                defer split_h2.deinit();

                if (split_h2.showFirst()) SceneTree.draw();
                if (split_h2.showSecond()) SceneViewport.draw();
            }

            if (split_v.showSecond()) {
                AssetBrowser.draw();
            }
        }

        if (split_h.showSecond()) {
            Inspector.draw();
        }
    }

    _ = dvui.separator(@src(), .{ .expand = .horizontal });
    TaskBar.draw();

    drawDragGhost();

    // Reap finished background jobs and keep frames flowing while one runs.
    Tasks.pump(dvui.io);

    // Step the in-editor game simulation (issue #31). Keeps frames flowing
    // while a scene is playing so the viewport animates continuously.
    PlayMode.pump(dvui.io);

    return true;
}

/// Small floating label that follows the cursor during asset / scene-node drags,
/// showing an icon and the name of the item being dragged.
fn drawDragGhost() void {
    if (EditorState.drag_kind == .none) return;

    // Change cursor to "move" while dragging.
    dvui.cursorSet(.arrow_all);

    // Position the ghost 12px below-right of the cursor.
    g_drag_ghost_rect.x = g_mouse_x + 12;
    g_drag_ghost_rect.y = g_mouse_y + 12;

    var fw = dvui.floatingWindow(@src(), .{
        .rect = &g_drag_ghost_rect,
        .resize = .none,
        .stay_above_parent_window = true,
        .window_avoid = .none,
    }, .{
        .background = true,
        .style = .window,
        .border = .all(1),
        .corner_radius = .all(4),
        .padding = .all(4),
    });
    defer fw.deinit();

    var row = dvui.box(@src(), .{ .dir = .horizontal }, .{ .background = false });
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
                .document => dvui.entypo.text_document,
                .code => dvui.entypo.code,
                .image => dvui.entypo.image,
                .sound => dvui.entypo.sound,
                .model => dvui.entypo.layers,
                .material => dvui.entypo.colours,
                .data => dvui.entypo.database,
            };
            dvui.icon(@src(), "di", icon_bytes, .{}, .{
                .min_size_content = .{ .w = 14, .h = 14 },
                .gravity_y = 0.5,
                .padding = .{ .w = 4 },
            });
            dvui.label(@src(), "{s}", .{name}, .{ .gravity_y = 0.5 });
        },
        .game_object => {
            const idx = EditorState.drag_object_idx;
            const name = if (idx < EditorState.object_count)
                EditorState.objects[idx].nameSlice()
            else
                "Object";
            dvui.icon(@src(), "di", dvui.entypo.layers, .{}, .{
                .min_size_content = .{ .w = 14, .h = 14 },
                .gravity_y = 0.5,
                .padding = .{ .w = 4 },
            });
            dvui.label(@src(), "{s}", .{name}, .{ .gravity_y = 0.5 });
        },
        .none => {},
    }
}
