const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const GpuRenderer = @import("GpuRenderer.zig");
const PlayMode = @import("PlayMode.zig");
const EditorState = @import("../services/EditorState.zig");
const GizmoSystem = @import("GizmoSystem.zig");
const EditorCamera = @import("EditorCamera.zig");
const AxisGizmo = @import("AxisGizmo.zig");
const UiOverlay = @import("../main-window/UiOverlay.zig");
const MenuItems = @import("../MenuItems.zig");
const ui_render = @import("ui_render");
const StudioLocale = @import("../services/StudioLocale.zig");
const Shortcuts = @import("../services/Shortcuts.zig");
const tr = StudioLocale.tr;

var hooks_installed = false;

// Matched locally via `Shortcuts.eventMatches` in `handleHotkeys` (contextual,
// not `dispatchGlobal`-routed), so these carry no registered `Handler`.
const viewport_commands = [_]Shortcuts.CommandDesc{
    .{ .id = "sceneView.translateMode", .title = "Move Tool", .context = .scene_viewport, .requires_focus = true, .defaults = &.{Shortcuts.Binding.single(.{ .key = .w })} },
    .{ .id = "sceneView.rotateMode", .title = "Rotate Tool", .context = .scene_viewport, .requires_focus = true, .defaults = &.{Shortcuts.Binding.single(.{ .key = .e })} },
    .{ .id = "sceneView.scaleMode", .title = "Scale Tool", .context = .scene_viewport, .requires_focus = true, .defaults = &.{Shortcuts.Binding.single(.{ .key = .r })} },
    .{ .id = "sceneView.focusSelection", .title = "Focus Selection", .context = .scene_viewport, .requires_focus = true, .defaults = &.{Shortcuts.Binding.single(.{ .key = .f })} },
};
const viewport_handlers = [_]?Shortcuts.Handler{null} ** viewport_commands.len;

/// "Show UI overlay" toggle: draws the scene's referenced
/// `.uidoc` documents (plus the one open in `UiDocumentEditor`) letterboxed
/// over the 3D viewport, WYSIWYG with what the shipped game will render via
/// the same `ui_render.drawTree` call (D9). See `UiOverlay.zig`.
var show_ui_overlay: bool = false;

/// Border tint shown around the viewport while a simulation runs:
/// orange while playing, blue while paused — a Unity-style visual play-state cue.
/// The Play transport controls live in the main menu bar (see MenuBar.zig);
/// the viewport only renders and shows this state-cue border.
const play_border = gui.Color{ .r = 240, .g = 130, .b = 30, .a = 255 };
const pause_border = gui.Color{ .r = 70, .g = 150, .b = 230, .a = 255 };

/// Per-Scene-instance camera + navigation state, so multiple Scene panels
/// can view/edit from several angles at once. Retained via dvui's own
/// per-widget storage (`dataGetPtrDefault`), keyed by the current
/// parent widget's id — the dockspace content box `Dockspace.openLeaf`
/// creates, which is already unique per dock-tab *instance* (not just per
/// panel type, see `Panels.newInstanceId`). `EditorCamera`'s pose lives in
/// that module's own globals only for the duration of one `draw()` call,
/// swapped in/out at the top/bottom — same pattern `Documents.zig` already
/// uses to give each open scene *tab* its own camera; this just keys by
/// dock instance instead. Selection and the object list stay global/shared
/// (Unity does the same — only the camera differs per Scene view).
const InstanceState = struct {
    cam: EditorCamera.State = .{},
    /// Persistent left-button state, so the transform gizmo can track a drag
    /// across frames (dvui delivers press/release as discrete events).
    left_down: bool = false,
    last_mouse: gui.Point.Physical = .{ .x = 0, .y = 0 },
    /// Persistent free-look navigation state (held across frames).
    rmb_down: bool = false,
    /// Persistent middle-button pan state (held across frames).
    mmb_down: bool = false,
    nav_fwd: bool = false,
    nav_back: bool = false,
    nav_left: bool = false,
    nav_right: bool = false,
    nav_up: bool = false,
    nav_down: bool = false,
    nav_fast: bool = false,
    /// Snap animation driven by clicking the axis-orientation gizmo.
    axis_anim: AxisGizmo.Anim = .{},
};

/// Camera navigation speeds are loaded from Settings once they are ready.
var cam_settings_loaded = false;

const CAM_MOVE_KEY = "editor.camera.move_speed";
const CAM_LOOK_KEY = "editor.camera.look_sensitivity";
const CAM_ZOOM_KEY = "editor.camera.zoom_speed";
const CAM_PAN_KEY = "editor.camera.pan_speed";

/// Draw the Scene panel — the edit-time 3D viewport: editor-camera
/// free-look, gizmo interaction, billboard icons. Always shows the editor
/// camera over `EditorState.objects`, even while a simulation runs — those
/// objects are a frozen snapshot during Play (the running sim owns its own
/// copy, see `PlayMode.zig`), so this is just Unity's "Scene tab stays
/// navigable during Play" behavior, not live gameplay state. The running
/// game's own camera lives in the separate `drawGame` panel.
pub fn draw() void {
    if (!hooks_installed) {
        hooks_installed = true;
        Shortcuts.register(&viewport_commands, &viewport_handlers);
    }

    // Must be captured before creating any widget below (which would
    // become the new "current parent") — this is the dockspace's per-tab
    // content box, unique per Scene *instance*.
    const inst_id = gui.parentGet().data().id;
    const inst = gui.dataGetPtrDefault(null, inst_id, "_scene_inst", InstanceState, .{});
    EditorCamera.setState(inst.cam);
    defer inst.cam = EditorCamera.getState();

    var vp = gui.box(@src(), .{}, .{
        .expand = .both,
        .background = true,
        .style = .app1,
    });
    defer vp.deinit();

    {
        var toolbar = gui.box(@src(), .{ .dir = .horizontal }, .{
            .expand = .horizontal,
            .border = .all(1),
            .background = true,
            .padding = .all(4),
        });
        defer toolbar.deinit();
        drawGizmoToolbar();
        _ = gui.checkbox(@src(), &show_ui_overlay, tr("Show UI overlay"), .{ .gravity_y = 0.5 });
    }

    var content = gui.box(@src(), .{}, .{ .expand = .both });
    defer content.deinit();

    // Drop a dragged scene/prefab asset here to instantiate it as a linked
    // prefab instance in the active scene.
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

    // ── Editor camera + gizmo interaction ────────────────────────────────────
    const phys = content.data().contentRectScale().r;

    // Free-look navigation drives the editor camera, independent of any
    // scene Camera component.
    EditorCamera.ensureInit(EditorState.objects, EditorState.object_count);
    loadCameraSettings();
    const nav = gatherNav(inst, content, phys);
    _ = EditorCamera.navigate(nav);
    handleHotkeys(inst, content, phys);

    const m = gatherMouse(inst, content, phys);

    // The axis gizmo sits on top of the rendered scene, so a click landing on
    // it must not also fall through to scene click-to-select below. Applying
    // the snap (if any) here, before the scene renders, keeps the gizmo dots
    // and the 3D view in lockstep instead of lagging a frame apart.
    const axis_hit = AxisGizmo.pick(nat_rect, phys, scale, m.pos.x, m.pos.y);
    AxisGizmo.applySnap(axis_hit, m.left_pressed, EditorState.objects, EditorState.object_count, &inst.axis_anim);
    GpuRenderer.setEditorCamera(EditorCamera.pose());

    var scene_m = m;
    if (axis_hit != null) scene_m.left_pressed = false;
    const cam = GpuRenderer.cameraFor(vp_w, vp_h);
    const grect = GizmoSystem.Rect{ .x = phys.x, .y = phys.y, .w = phys.w, .h = phys.h };
    GizmoSystem.update(cam, grect, EditorState.objects, EditorState.object_count, scene_m);
    GpuRenderer.setGizmosEnabled(true);

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
        drawIcons(vp_w, vp_h, nat_rect);
        AxisGizmo.draw(nat_rect, phys, scale, axis_hit);
        if (show_ui_overlay) {
            UiOverlay.drawSceneOverlay(.{ .w = nat_rect.w, .h = nat_rect.h });
        }
    } else {
        gui.label(@src(), "{s}", .{tr("3D viewport unavailable")}, .{
            .gravity_x = 0.5,
            .gravity_y = 0.5,
            .expand = .both,
        });
    }
}

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

/// Draw billboard icons (lights, cameras, custom) as a dvui overlay over the
/// rendered scene. Selection itself is handled by the gizmo system's pick — the
/// icons are a visual cue marking where those objects are.
fn drawIcons(vp_w: u32, vp_h: u32, nat_rect: gui.Rect) void {
    const cam = GpuRenderer.cameraFor(vp_w, vp_h);
    const grect = GizmoSystem.Rect{ .x = 0, .y = 0, .w = nat_rect.w, .h = nat_rect.h };
    var buf: [128]GizmoSystem.IconPlacement = undefined;
    const n = GizmoSystem.collectIcons(cam, grect, EditorState.objects, EditorState.object_count, &buf);
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
/// are tracked per-instance (so a drag continues off-widget); a press only
/// counts when it lands inside the viewport.
fn gatherMouse(inst: *InstanceState, content: *gui.BoxWidget, phys: gui.Rect.Physical) GizmoSystem.MouseInput {
    var pressed = false;
    var released = false;
    for (gui.events()) |*e| {
        if (e.evt != .mouse) continue;
        const me = e.evt.mouse;
        switch (me.action) {
            .position => inst.last_mouse = me.p,
            .motion => inst.last_mouse = me.p,
            .press => if (me.button == .left and gui.eventMatchSimple(e, content.data())) {
                inst.last_mouse = me.p;
                pressed = true;
                inst.left_down = true;
            },
            .release => if (me.button == .left) {
                released = inst.left_down;
                inst.left_down = false;
            },
            else => {},
        }
    }
    return .{
        .pos = .{ .x = inst.last_mouse.x, .y = inst.last_mouse.y },
        .inside = phys.contains(inst.last_mouse),
        .left_pressed = pressed,
        .left_down = inst.left_down,
        .left_released = released,
    };
}

/// Gizmo mode + snap + visibility controls.
fn drawGizmoToolbar() void {
    _ = gui.spacer(@src(), .{ .expand = .horizontal });

    const G = GizmoSystem;
    if (modeButton(tr("Move"), G.mode == .translate, 1)) G.mode = .translate;
    if (modeButton(tr("Rotate"), G.mode == .rotate, 2)) G.mode = .rotate;
    if (modeButton(tr("Scale"), G.mode == .scale, 3)) G.mode = .scale;

    _ = gui.spacer(@src(), .{ .min_size_content = .{ .w = 8 } });
    _ = gui.checkbox(@src(), &G.snap_enabled, tr("Snap"), .{ .gravity_y = 0.5 });

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
    if (MenuItems.dropdown(@src(), tr("Camera"), .{ .id_extra = 1 })) |r| {
        var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(.{ .x = r.x, .y = r.y + r.h }) }, .{});
        defer fw.deinit();

        var changed = false;
        if (speedRow(tr("Move speed"), &EditorCamera.move_speed, 0.5, 40, 0.5, "{d:0.1}", 1)) changed = true;
        if (speedRow(tr("Look sens."), &EditorCamera.look_sensitivity, 0.02, 1.0, 0.01, "{d:0.2}", 2)) changed = true;
        // Plain number field, not a slider: the useful range spans two orders
        // of magnitude (a single wheel notch should stay a small nudge even at
        // close range), which a slider's fixed drag distance can't resolve
        // precisely — dragging it barely moves the value at the low end.
        if (numberRow(tr("Zoom speed"), &EditorCamera.zoom_speed, 0.001, 4.0, 3)) changed = true;
        if (speedRow(tr("Pan speed"), &EditorCamera.pan_speed, 0.002, 0.1, 0.001, "{d:0.3}", 4)) changed = true;

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

/// One labeled plain-number-entry row (no slider) inside the Camera menu —
/// for values where precise small entries matter more than drag-friendliness.
fn numberRow(name: []const u8, value: *f32, min: f32, max: f32, id: usize) bool {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .id_extra = id, .expand = .horizontal });
    defer row.deinit();
    gui.label(@src(), "{s}", .{name}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 80 } });
    const result = gui.textEntryNumber(@src(), f32, .{
        .value = value,
        .min = min,
        .max = max,
    }, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 110 } });
    return result.changed;
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
    EditorCamera.pan_speed = @floatCast(s.getFloat(CAM_PAN_KEY, EditorCamera.pan_speed));
}

fn saveCameraSettings() void {
    if (!EditorState.settingsReady()) return;
    const s = &EditorState.settings;
    s.setFloat(CAM_MOVE_KEY, EditorCamera.move_speed) catch {};
    s.setFloat(CAM_LOOK_KEY, EditorCamera.look_sensitivity) catch {};
    s.setFloat(CAM_ZOOM_KEY, EditorCamera.zoom_speed) catch {};
    s.setFloat(CAM_PAN_KEY, EditorCamera.pan_speed) catch {};
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
    if (MenuItems.dropdown(@src(), tr("Gizmos"), .{ .id_extra = 2 })) |r| {
        var fw = gui.floatingMenu(@src(), .{ .from = gui.Rect.Natural.fromPoint(.{ .x = r.x, .y = r.y + r.h }) }, .{});
        defer fw.deinit();
        const S = &GizmoSystem.show;
        _ = gui.checkbox(@src(), &S.transform_gizmo, tr("Transform handles"), .{});
        _ = gui.checkbox(@src(), &S.cameras, tr("Cameras"), .{});
        _ = gui.checkbox(@src(), &S.lights, tr("Lights"), .{});
        _ = gui.checkbox(@src(), &S.colliders, tr("Colliders"), .{});
        _ = gui.checkbox(@src(), &S.custom, tr("Custom"), .{});
        _ = gui.checkbox(@src(), &S.icons, tr("Icons"), .{});
        _ = gui.checkbox(@src(), &S.selection, tr("Selection outline"), .{});
        _ = gui.checkbox(@src(), &S.grid, tr("Grid"), .{});
    }
}

/// Collect this frame's free-look navigation input. Held movement keys persist
/// across frames; look deltas and wheel are per-frame. Movement is only applied
/// while the right mouse button is held (see `EditorCamera`).
fn gatherNav(inst: *InstanceState, content: *gui.BoxWidget, phys: gui.Rect.Physical) EditorCamera.Nav {
    var look_dx: f32 = 0;
    var look_dy: f32 = 0;
    var pan_dx: f32 = 0;
    var pan_dy: f32 = 0;
    var wheel: f32 = 0;
    for (gui.events()) |*e| {
        switch (e.evt) {
            .key => |ke| {
                if (ke.action == .repeat) continue;
                const pressed = ke.action != .up;
                switch (ke.code) {
                    .w => inst.nav_fwd = pressed,
                    .s => inst.nav_back = pressed,
                    .a => inst.nav_left = pressed,
                    .d => inst.nav_right = pressed,
                    .e => inst.nav_up = pressed,
                    .q => inst.nav_down = pressed,
                    .left_shift, .right_shift => inst.nav_fast = pressed,
                    else => {},
                }
            },
            .mouse => |me| switch (me.action) {
                .press => {
                    if (me.button == .right and gui.eventMatchSimple(e, content.data())) {
                        inst.rmb_down = true;
                    }
                    if (me.button == .middle and gui.eventMatchSimple(e, content.data())) {
                        inst.mmb_down = true;
                    }
                },
                .release => {
                    if (me.button == .right) inst.rmb_down = false;
                    if (me.button == .middle) inst.mmb_down = false;
                },
                .motion => |delta| {
                    if (inst.rmb_down) {
                        look_dx += delta.x;
                        look_dy += delta.y;
                    }
                    if (inst.mmb_down) {
                        pan_dx += delta.x;
                        pan_dy += delta.y;
                    }
                },
                .wheel_y => |amt| if (phys.contains(inst.last_mouse)) {
                    wheel += amt;
                },
                else => {},
            },
            else => {},
        }
    }
    return .{
        .rmb_down = inst.rmb_down,
        .look_dx = look_dx,
        .look_dy = look_dy,
        .pan_dx = pan_dx,
        .pan_dy = pan_dy,
        .wheel = wheel,
        .forward = inst.nav_fwd,
        .back = inst.nav_back,
        .left = inst.nav_left,
        .right = inst.nav_right,
        .up = inst.nav_up,
        .down = inst.nav_down,
        .fast = inst.nav_fast,
        .dt = gui.secondsSinceLastFrame(),
    };
}

/// Frame the editor camera on the primary selection (F key).
/// `EditorCamera.focusOn` writes through the globals `draw()` already
/// swapped this instance's state into — no `InstanceState` needed here.
fn focusSelection() void {
    const sel = EditorState.selected_object orelse return;
    if (sel >= EditorState.object_count) return;
    const t = &EditorState.objects[sel].transform;
    const extent = @max(@abs(t.scale.x), @max(@abs(t.scale.y), @abs(t.scale.z)));
    EditorCamera.focusOn(t.position, extent * 3.0 + 2.0);
}

/// W = move, E = rotate, R = scale (Unity convention), F = focus selection.
/// Mode-switch keys are suppressed while the right mouse button is held,
/// when W/E act as free-look movement instead (see `gatherNav`); F is not,
/// since focusing while orbiting is still useful. All four require the
/// mouse hovering *this* viewport instance (`Shortcuts.eventMatches`'s
/// `requires_focus`) and no text field mid-edit.
fn handleHotkeys(inst: *InstanceState, content: *gui.BoxWidget, phys: gui.Rect.Physical) void {
    const hovered = phys.contains(inst.last_mouse);
    for (gui.events()) |*e| {
        if (!inst.rmb_down) {
            if (Shortcuts.eventMatches(e, "sceneView.translateMode", hovered)) {
                e.handle(@src(), content.data());
                GizmoSystem.mode = .translate;
                continue;
            }
            if (Shortcuts.eventMatches(e, "sceneView.rotateMode", hovered)) {
                e.handle(@src(), content.data());
                GizmoSystem.mode = .rotate;
                continue;
            }
            if (Shortcuts.eventMatches(e, "sceneView.scaleMode", hovered)) {
                e.handle(@src(), content.data());
                GizmoSystem.mode = .scale;
                continue;
            }
        }
        if (Shortcuts.eventMatches(e, "sceneView.focusSelection", hovered)) {
            e.handle(@src(), content.data());
            focusSelection();
        }
    }
}
