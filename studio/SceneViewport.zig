const gui = @import("gui");
const GpuRenderer = @import("GpuRenderer.zig");
const PlayMode = @import("PlayMode.zig");
const EditorState = @import("EditorState.zig");
const GizmoSystem = @import("GizmoSystem.zig");
const EditorCamera = @import("EditorCamera.zig");

/// Border tint shown around the viewport while a simulation runs (issue #31):
/// orange while playing, blue while paused — a Unity-style visual play-state cue.
/// The Play transport controls live in the main menu bar (see MenuBar.zig);
/// the viewport only renders and shows this state-cue border.
const play_border = gui.Color{ .r = 240, .g = 130, .b = 30, .a = 255 };
const pause_border = gui.Color{ .r = 70, .g = 150, .b = 230, .a = 255 };

/// Persistent left-button state, so the transform gizmo can track a drag across
/// frames (dvui delivers press/release as discrete events).
var left_down: bool = false;
var last_mouse: gui.Point.Physical = .{ .x = 0, .y = 0 };

/// Persistent free-look navigation state (held across frames).
var rmb_down: bool = false;
var nav_fwd = false;
var nav_back = false;
var nav_left = false;
var nav_right = false;
var nav_up = false;
var nav_down = false;
var nav_fast = false;

/// Camera navigation speeds are loaded from Settings once they are ready.
var cam_settings_loaded = false;

const CAM_MOVE_KEY = "editor.camera.move_speed";
const CAM_LOOK_KEY = "editor.camera.look_sensitivity";
const CAM_ZOOM_KEY = "editor.camera.zoom_speed";

/// Draw the 3D scene viewport using the GPU renderer.
pub fn draw() void {
    var vp = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .window,
    });
    defer vp.deinit();

    {
        var header = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(4),
        });
        defer header.deinit();
        gui.label(@src(), "Scene View", .{}, .{ .font = .theme(.heading), .gravity_y = 0.5 });
        if (PlayMode.state() == .edit) drawGizmoToolbar();
    }

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

    // Drop a dragged scene/prefab asset here to instantiate it as a linked
    // prefab instance in the active scene (issue #32, #24).
    if (EditorState.drag_kind == .asset) {
        for (gui.events()) |*e| {
            if (!gui.eventMatchSimple(e, content.data())) continue;
            if (e.evt == .mouse) {
                const me = e.evt.mouse;
                if (me.action == .release and me.button == .left) {
                    const path = EditorState.dragAssetPath();
                    if (EditorState.assetDbReady() and path.len > 0) {
                        if (EditorState.asset_db.findByPath(path)) |info| {
                            if (info.asset_type == .scene) {
                                e.handle(@src(), content.data());
                                _ = EditorState.instantiatePrefab(gui.frameTimeNS(), gui.io, path);
                            }
                        }
                    }
                    EditorState.clearDrag();
                }
            }
        }
    }

    const scale = gui.windowNaturalScale();
    const nat_rect = content.wd.rect;
    const vp_w: u32 = @max(1, @as(u32, @intFromFloat(nat_rect.w * scale)));
    const vp_h: u32 = @max(1, @as(u32, @intFromFloat(nat_rect.h * scale)));

    // ── Editor camera + gizmo interaction (edit mode only) ──────────────────────
    if (st == .edit) {
        const phys = content.data().contentRectScale().r;

        // Free-look navigation drives the editor camera, independent of any
        // scene Camera component.
        EditorCamera.ensureInit(&EditorState.objects, EditorState.object_count);
        loadCameraSettings();
        const nav = gatherNav(content, phys);
        _ = EditorCamera.navigate(nav);
        GpuRenderer.setEditorCamera(EditorCamera.pose());
        handleHotkeys();

        const m = gatherMouse(content, phys);
        const cam = GpuRenderer.cameraFor(vp_w, vp_h);
        const grect = GizmoSystem.Rect{ .x = phys.x, .y = phys.y, .w = phys.w, .h = phys.h };
        GizmoSystem.update(cam, grect, &EditorState.objects, EditorState.object_count, m);
        GpuRenderer.setGizmosEnabled(true);
    } else {
        // Play mode shows the running game's own camera again.
        GpuRenderer.setEditorCamera(null);
        GpuRenderer.setGizmosEnabled(false);
    }

    // The image and the billboard-icon overlay share an overlay container so the
    // icons stack on top of the rendered scene instead of below it.
    var stack = gui.overlay(@src(), .{ .expand = .both });
    defer stack.deinit();

    if (GpuRenderer.renderViewport(vp_w, vp_h)) |target| {
        const tex = gui.Texture.fromTargetTemp(target) catch return;
        _ = gui.image(@src(), .{
            .source = .{ .texture = tex },
        }, .{
            .expand = .both,
            .gravity_x = 0.5,
            .gravity_y = 0.5,
        });
        if (st == .edit) drawIcons(vp_w, vp_h, nat_rect);
    } else {
        gui.label(@src(), "3D viewport unavailable", .{}, .{
            .gravity_x = 0.5,
            .gravity_y = 0.5,
            .expand = .both,
        });
    }
}

/// Draw billboard icons (lights, cameras, custom) as a dvui overlay over the
/// rendered scene. Selection itself is handled by the gizmo system's pick — the
/// icons are a visual cue marking where those objects are.
fn drawIcons(vp_w: u32, vp_h: u32, nat_rect: gui.Rect) void {
    const cam = GpuRenderer.cameraFor(vp_w, vp_h);
    const grect = GizmoSystem.Rect{ .x = 0, .y = 0, .w = nat_rect.w, .h = nat_rect.h };
    var buf: [128]GizmoSystem.IconPlacement = undefined;
    const n = GizmoSystem.collectIcons(cam, grect, &EditorState.objects, EditorState.object_count, &buf);
    for (buf[0..n], 0..) |ic, i| {
        const col = gui.Color{
            .r = @intFromFloat(@max(0, @min(1, ic.color.r)) * 255),
            .g = @intFromFloat(@max(0, @min(1, ic.color.g)) * 255),
            .b = @intFromFloat(@max(0, @min(1, ic.color.b)) * 255),
            .a = 255,
        };
        gui.icon(@src(), "giz_icon", ic.glyph, .{ .fill_color = col }, .{
            .id_extra = i,
            .rect = .{ .x = ic.x - 9, .y = ic.y - 9, .w = 18, .h = 18 },
        });
    }
}

/// Collect this frame's mouse state for the gizmo system. Position and release
/// are tracked globally (so a drag continues off-widget); a press only counts
/// when it lands inside the viewport.
fn gatherMouse(content: *gui.BoxWidget, phys: gui.Rect.Physical) GizmoSystem.MouseInput {
    var pressed = false;
    var released = false;
    for (gui.events()) |*e| {
        if (e.evt != .mouse) continue;
        const me = e.evt.mouse;
        switch (me.action) {
            .position => last_mouse = me.p,
            .motion => last_mouse = me.p,
            .press => if (me.button == .left and gui.eventMatchSimple(e, content.data())) {
                last_mouse = me.p;
                pressed = true;
                left_down = true;
            },
            .release => if (me.button == .left) {
                released = left_down;
                left_down = false;
            },
            else => {},
        }
    }
    return .{
        .pos = .{ .x = last_mouse.x, .y = last_mouse.y },
        .inside = phys.contains(last_mouse),
        .left_pressed = pressed,
        .left_down = left_down,
        .left_released = released,
    };
}

/// Gizmo mode + snap + visibility controls.
fn drawGizmoToolbar() void {
    _ = gui.spacer(@src(), .{ .expand = .horizontal });

    const G = GizmoSystem;
    if (modeButton("Move", G.mode == .translate, 1)) G.mode = .translate;
    if (modeButton("Rotate", G.mode == .rotate, 2)) G.mode = .rotate;
    if (modeButton("Scale", G.mode == .scale, 3)) G.mode = .scale;

    _ = gui.spacer(@src(), .{ .min_size_content = .{ .w = 8 } });
    _ = gui.checkbox(@src(), &G.snap_enabled, "Snap", .{ .gravity_y = 0.5 });

    _ = gui.spacer(@src(), .{ .min_size_content = .{ .w = 8 } });
    drawVisibilityMenu();

    _ = gui.spacer(@src(), .{ .min_size_content = .{ .w = 8 } });
    drawCameraMenu();
}

/// Free-look camera speed tuning. Sliders edit the live `EditorCamera` values;
/// any change is persisted through the Settings API so the feel sticks across
/// sessions (zoom in particular is easy to make too aggressive by default).
fn drawCameraMenu() void {
    var m = gui.menu(@src(), .horizontal, .{ .gravity_y = 0.5 });
    defer m.deinit();
    if (gui.menuItemLabel(@src(), "Camera ▾", .{ .submenu = true }, .{})) |r| {
        var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(.{ .x = r.x, .y = r.y + r.h }) }, .{});
        defer fw.deinit();

        var changed = false;
        if (speedRow("Move speed", &EditorCamera.move_speed, 0.5, 40, 0.5, "{d:0.1}", 1)) changed = true;
        if (speedRow("Look sens.", &EditorCamera.look_sensitivity, 0.02, 1.0, 0.01, "{d:0.2}", 2)) changed = true;
        if (speedRow("Zoom speed", &EditorCamera.zoom_speed, 0.05, 4.0, 0.05, "{d:0.2}", 3)) changed = true;

        if (changed) saveCameraSettings();
    }
}

/// One labeled slider row inside the Camera menu.
fn speedRow(
    name: []const u8,
    value: *f32,
    min: f32,
    max: f32,
    interval: f32,
    comptime fmt: []const u8,
    id: usize,
) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .id_extra = id, .expand = .horizontal });
    defer row.deinit();
    gui.label(@src(), "{s}", .{name}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 80 } });
    return gui.sliderEntry(@src(), fmt, .{
        .value = value,
        .min = min,
        .max = max,
        .interval = interval,
    }, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 110 } });
}

/// Load camera navigation speeds from Settings once the store is ready (it is
/// not during the very first frames before a project/global load completes).
fn loadCameraSettings() void {
    if (cam_settings_loaded or !EditorState.settingsReady()) return;
    cam_settings_loaded = true;
    const s = &EditorState.settings;
    EditorCamera.move_speed = @floatCast(s.getFloat(CAM_MOVE_KEY, EditorCamera.move_speed));
    EditorCamera.look_sensitivity = @floatCast(s.getFloat(CAM_LOOK_KEY, EditorCamera.look_sensitivity));
    EditorCamera.zoom_speed = @floatCast(s.getFloat(CAM_ZOOM_KEY, EditorCamera.zoom_speed));
}

fn saveCameraSettings() void {
    if (!EditorState.settingsReady()) return;
    const s = &EditorState.settings;
    s.setFloat(CAM_MOVE_KEY, EditorCamera.move_speed) catch {};
    s.setFloat(CAM_LOOK_KEY, EditorCamera.look_sensitivity) catch {};
    s.setFloat(CAM_ZOOM_KEY, EditorCamera.zoom_speed) catch {};
    s.save(gui.io);
}

fn modeButton(text: []const u8, active: bool, id: usize) bool {
    return gui.button(@src(), text, .{}, .{
        .id_extra = id,
        .gravity_y = 0.5,
        .style = if (active) .highlight else .control,
    });
}

fn drawVisibilityMenu() void {
    var m = gui.menu(@src(), .horizontal, .{ .gravity_y = 0.5 });
    defer m.deinit();
    if (gui.menuItemLabel(@src(), "Gizmos ▾", .{ .submenu = true }, .{})) |r| {
        var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(.{ .x = r.x, .y = r.y + r.h }) }, .{});
        defer fw.deinit();
        const S = &GizmoSystem.show;
        _ = gui.checkbox(@src(), &S.transform_gizmo, "Transform handles", .{});
        _ = gui.checkbox(@src(), &S.cameras, "Cameras", .{});
        _ = gui.checkbox(@src(), &S.lights, "Lights", .{});
        _ = gui.checkbox(@src(), &S.colliders, "Colliders", .{});
        _ = gui.checkbox(@src(), &S.custom, "Custom", .{});
        _ = gui.checkbox(@src(), &S.icons, "Icons", .{});
        _ = gui.checkbox(@src(), &S.selection, "Selection outline", .{});
        _ = gui.checkbox(@src(), &S.grid, "Grid", .{});
    }
}

/// Collect this frame's free-look navigation input. Held movement keys persist
/// across frames; look deltas and wheel are per-frame. Movement is only applied
/// while the right mouse button is held (see `EditorCamera`).
fn gatherNav(content: *gui.BoxWidget, phys: gui.Rect.Physical) EditorCamera.Nav {
    var look_dx: f32 = 0;
    var look_dy: f32 = 0;
    var wheel: f32 = 0;
    for (gui.events()) |*e| {
        switch (e.evt) {
            .key => |ke| {
                if (ke.action == .repeat) continue;
                const pressed = ke.action != .up;
                switch (ke.code) {
                    .w => nav_fwd = pressed,
                    .s => nav_back = pressed,
                    .a => nav_left = pressed,
                    .d => nav_right = pressed,
                    .e => nav_up = pressed,
                    .q => nav_down = pressed,
                    .left_shift, .right_shift => nav_fast = pressed,
                    .f => if (pressed) focusSelection(),
                    else => {},
                }
            },
            .mouse => |me| switch (me.action) {
                .press => if (me.button == .right and gui.eventMatchSimple(e, content.data())) {
                    rmb_down = true;
                },
                .release => if (me.button == .right) {
                    rmb_down = false;
                },
                .motion => |delta| if (rmb_down) {
                    look_dx += delta.x;
                    look_dy += delta.y;
                },
                .wheel_y => |amt| if (phys.contains(last_mouse)) {
                    wheel += amt;
                },
                else => {},
            },
            else => {},
        }
    }
    return .{
        .rmb_down = rmb_down,
        .look_dx = look_dx,
        .look_dy = look_dy,
        .wheel = wheel,
        .forward = nav_fwd,
        .back = nav_back,
        .left = nav_left,
        .right = nav_right,
        .up = nav_up,
        .down = nav_down,
        .fast = nav_fast,
        .dt = gui.secondsSinceLastFrame(),
    };
}

/// Frame the editor camera on the primary selection (F key).
fn focusSelection() void {
    const sel = EditorState.selected_object orelse return;
    if (sel >= EditorState.object_count) return;
    const t = &EditorState.objects[sel].transform;
    const extent = @max(@abs(t.scale.x), @max(@abs(t.scale.y), @abs(t.scale.z)));
    EditorCamera.focusOn(t.position, extent * 3.0 + 2.0);
}

/// W = move, E = rotate, R = scale (Unity convention). Suppressed while the
/// right mouse button is held, when W/E act as free-look movement keys instead.
fn handleHotkeys() void {
    if (rmb_down) return;
    for (gui.events()) |*e| {
        if (e.evt != .key) continue;
        const ke = e.evt.key;
        if (ke.action == .up) continue;
        if (ke.mod.control() or ke.mod.command() or ke.mod.alt()) continue;
        switch (ke.code) {
            .w => GizmoSystem.mode = .translate,
            .e => GizmoSystem.mode = .rotate,
            .r => GizmoSystem.mode = .scale,
            else => {},
        }
    }
}
