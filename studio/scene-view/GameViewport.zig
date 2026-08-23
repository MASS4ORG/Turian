const gui = @import("gui");
const PlayMode = @import("PlayMode.zig");
const GpuRenderer = @import("GpuRenderer.zig");
const ui_render = @import("ui_render");
const UiOverlay = @import("../main-window/UiOverlay.zig");
const StudioLocale = @import("../services/StudioLocale.zig");
const tr = StudioLocale.tr;

/// Border tint shown around the viewport while a simulation runs:
/// orange while playing, blue while paused — a Unity-style visual play-state cue.
/// The Play transport controls live in the main menu bar (see MenuBar.zig);
/// the viewport only renders and shows this state-cue border.
const play_border = gui.Color{ .r = 240, .g = 130, .b = 30, .a = 255 };
const pause_border = gui.Color{ .r = 70, .g = 150, .b = 230, .a = 255 };

/// Draw the Game panel — dedicated Play-mode viewport, split from Scene:
/// the running simulation's own camera + live in-game GUI, Unity-style — a
/// "Display 1 / No cameras rendering" placeholder outside Play, matching
/// Unity's Game tab when nothing is running.
pub fn drawGame() void {
    var vp = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .app1,
    });
    defer vp.deinit();

    const st = PlayMode.state();
    const border_color: ?gui.Color = switch (st) {
        .edit => null,
        .playing => play_border,
        .paused => pause_border,
    };

    var content = gui.box(@src(), .{}, .{
        .expand = .both,
        .border = if (border_color != null) .all(3) else .all(0),
        .color_border = border_color,
    });
    defer content.deinit();

    if (st == .edit) {
        var center = gui.box(@src(), .{}, .{ .expand = .both, .gravity_x = 0.5, .gravity_y = 0.5 });
        defer center.deinit();
        gui.label(@src(), "{s}", .{tr("Display 1")}, .{ .gravity_x = 0.5, .font = .theme(.heading) });
        gui.label(@src(), "{s}", .{tr("No cameras rendering")}, .{ .gravity_x = 0.5 });
        return;
    }

    const scale = gui.windowNaturalScale();
    const nat_rect = content.wd.rect;
    const vp_w: u32 = @max(1, @as(u32, @intFromFloat(nat_rect.w * scale)));
    const vp_h: u32 = @max(1, @as(u32, @intFromFloat(nat_rect.h * scale)));

    if (GpuRenderer.renderGameViewport(PlayMode.playNodes(), vp_w, vp_h)) |target| {
        const tex = gui.Texture.fromTargetTemp(target) catch return;
        _ = gui.image(@src(), .{
            .source = .{ .texture = tex },
        }, .{
            .expand = .both,
            .gravity_x = 0.5,
            .gravity_y = 0.5,
        });
        // Live GUI during Play/Paused. Runs before `PlayMode.pump()`
        // (see Window.zig's per-frame order) so `bw.processEvents()` below
        // can claim the click first — the same input-priority ordering
        // `PlayMode.feedInput`'s `e.handled` check expects.
        drawPlayModeUi(.{ .w = nat_rect.w, .h = nat_rect.h });
    } else {
        gui.label(@src(), "{s}", .{tr("3D viewport unavailable")}, .{
            .gravity_x = 0.5,
            .gravity_y = 0.5,
            .expand = .both,
        });
    }
}

/// Draw + dispatch the running game's live `.uidoc` instances during
/// Play/Paused, reading the play library's `UiRuntime`/`UiEvents` (same
/// process, populated by `PlayMode.loadUiDocuments` at Play start).
fn drawPlayModeUi(target: gui.Rect) void {
    const rt = PlayMode.uiRuntime() orelse return;
    const events = PlayMode.uiEvents() orelse return;
    const channels = PlayMode.gameEvents();
    for (rt.instances()) |*entry| {
        if (!entry.instance.visible) continue;
        const lb = ui_render.fit(.{ .w = target.w, .h = target.h }, &entry.instance.doc);
        const result = ui_render.drawTree(&entry.instance.doc, lb, .{
            .texture_source = UiOverlay.resolveTextureBytes,
            .font_source = UiOverlay.resolveTextureBytes,
        });
        ui_render.dispatchClicks(&entry.instance.doc, result, entry.instance.resolved, events, channels);
    }
}
