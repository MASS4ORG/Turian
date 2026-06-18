const dvui = @import("dvui");
const GpuRenderer = @import("GpuRenderer.zig");
const PlayMode = @import("PlayMode.zig");
const EditorState = @import("EditorState.zig");

/// Border tint shown around the viewport while a simulation runs (issue #31):
/// orange while playing, blue while paused — a Unity-style visual play-state cue.
/// The Play transport controls live in the main menu bar (see MenuBar.zig);
/// the viewport only renders and shows this state-cue border.
const play_border = dvui.Color{ .r = 240, .g = 130, .b = 30, .a = 255 };
const pause_border = dvui.Color{ .r = 70, .g = 150, .b = 230, .a = 255 };

/// Draw the 3D scene viewport using the GPU renderer.
pub fn draw() void {
    var vp = dvui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer vp.deinit();

    {
        var header = dvui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(4),
        });
        defer header.deinit();
        dvui.label(@src(), "Scene View", .{}, .{ .font = .theme(.heading), .gravity_y = 0.5 });
    }

    const st = PlayMode.state();
    const border_color: ?dvui.Color = switch (st) {
        .edit => null,
        .playing => play_border,
        .paused => pause_border,
    };

    var content = dvui.box(@src(), .{}, .{
        .expand = .both,
        .border = if (border_color != null) .all(3) else .all(0),
        .color_border = border_color,
    });
    defer content.deinit();

    // Drop a dragged scene/prefab asset here to instantiate it as a linked
    // prefab instance in the active scene (issue #32, #24).
    if (EditorState.drag_kind == .asset) {
        for (dvui.events()) |*e| {
            if (!dvui.eventMatchSimple(e, content.data())) continue;
            if (e.evt == .mouse) {
                const me = e.evt.mouse;
                if (me.action == .release and me.button == .left) {
                    const path = EditorState.dragAssetPath();
                    if (EditorState.assetDbReady() and path.len > 0) {
                        if (EditorState.asset_db.findByPath(path)) |info| {
                            if (info.asset_type == .scene) {
                                e.handle(@src(), content.data());
                                _ = EditorState.instantiatePrefab(dvui.frameTimeNS(), dvui.io, path);
                            }
                        }
                    }
                    EditorState.clearDrag();
                }
            }
        }
    }

    const scale = dvui.windowNaturalScale();
    const nat_rect = content.wd.rect;
    const vp_w: u32 = @max(1, @as(u32, @intFromFloat(nat_rect.w * scale)));
    const vp_h: u32 = @max(1, @as(u32, @intFromFloat(nat_rect.h * scale)));

    if (GpuRenderer.renderViewport(vp_w, vp_h)) |target| {
        const tex = dvui.Texture.fromTargetTemp(target) catch return;
        _ = dvui.image(@src(), .{
            .source = .{ .texture = tex },
        }, .{
            .expand = .both,
            .gravity_x = 0.5,
            .gravity_y = 0.5,
        });
    } else {
        dvui.label(@src(), "3D viewport unavailable", .{}, .{
            .gravity_x = 0.5,
            .gravity_y = 0.5,
            .expand = .both,
        });
    }
}
