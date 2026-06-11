const dvui = @import("dvui");
const GpuRenderer = @import("GpuRenderer.zig");

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
            .padding = .all(6),
        });
        defer header.deinit();
        dvui.label(@src(), "Scene View", .{}, .{ .font = .theme(.heading) });
    }

    var content = dvui.box(@src(), .{}, .{ .expand = .both });
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
}
