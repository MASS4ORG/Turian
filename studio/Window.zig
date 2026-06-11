const dvui = @import("dvui");
const MenuBar = @import("MenuBar.zig");
const SceneTree = @import("SceneTree.zig");
const Inspector = @import("Inspector.zig");
const AssetBrowser = @import("AssetBrowser.zig");
const SceneViewport = @import("SceneViewport.zig");
const EditorState = @import("EditorState.zig");
const TaskBar = @import("TaskBar.zig");
const Tasks = @import("Tasks.zig");

var should_quit: bool = false;
var mouse_left_held: bool = false;
var g_inspector_ratio: f32 = 0.7;

/// Draw one frame of the editor UI. Returns true to continue, false to quit.
pub fn frame() bool {
    for (dvui.events()) |*e| {
        if (e.evt == .window and e.evt.window.action == .close) return false;
        if (e.evt == .app and e.evt.app.action == .quit) return false;
        switch (e.evt) {
            .mouse => |me| if (me.button == .left) {
                if (me.action == .press) mouse_left_held = true;
                if (me.action == .release) mouse_left_held = false;
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

    // Reap finished background jobs and keep frames flowing while one runs.
    Tasks.pump(dvui.io);

    return true;
}
