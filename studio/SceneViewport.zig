const dvui = @import("dvui");
const GpuRenderer = @import("GpuRenderer.zig");
const PlayMode = @import("PlayMode.zig");

/// Border tint shown around the viewport while a simulation runs (issue #31):
/// orange while playing, blue while paused — a Unity-style visual play-state cue.
const play_border = dvui.Color{ .r = 240, .g = 130, .b = 30, .a = 255 };
const pause_border = dvui.Color{ .r = 70, .g = 150, .b = 230, .a = 255 };

/// Draw the 3D scene viewport (with the Play toolbar) using the GPU renderer.
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
        _ = dvui.spacer(@src(), .{ .expand = .horizontal });
        drawControls();
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

    // Stats overlay (issue #2 reuse): FPS in the top-left corner while playing.
    if (st != .edit) {
        dvui.label(@src(), "{d:.0} FPS", .{PlayMode.fps()}, .{
            .gravity_x = 0.0,
            .gravity_y = 0.0,
            .margin = .all(8),
            .padding = .{ .x = 8, .y = 4, .w = 8, .h = 4 },
            .background = true,
            .corner_radius = .all(4),
            .font = .theme(.heading),
        });
    }
}

/// Play / Pause / Step / Stop controls, reflecting the current state machine.
fn drawControls() void {
    switch (PlayMode.state()) {
        .edit => {
            if (dvui.button(@src(), "▶ Play", .{}, .{ .style = .highlight, .gravity_y = 0.5 }))
                PlayMode.play(dvui.io);
        },
        .playing => {
            if (dvui.button(@src(), "⏸ Pause", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.pause();
            if (dvui.button(@src(), "⏹ Stop", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.stop();
        },
        .paused => {
            if (dvui.button(@src(), "▶ Resume", .{}, .{ .style = .highlight, .gravity_y = 0.5 }))
                PlayMode.play(dvui.io);
            if (dvui.button(@src(), "⏭ Step", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.step();
            if (dvui.button(@src(), "⏹ Stop", .{}, .{ .gravity_y = 0.5 }))
                PlayMode.stop();
        },
    }
}
