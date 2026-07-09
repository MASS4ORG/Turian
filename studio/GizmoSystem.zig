//! Editor gizmo system: builds the per-frame gizmo line buffers and
//! drives the interactive transform gizmo (move / rotate / scale with snapping).
//!
//! Two buffers are produced each frame and handed to the `render` module:
//!   • `world`   — component visualizers + selection wires, depth-tested.
//!   • `overlay` — the transform manipulation handles, always drawn on top.
//!
//! Built-in component gizmos (camera frustum, light shape, collider bounds) are
//! drawn directly here. Custom gizmos for user-script components can be
//! registered via `registerGizmo`, so components/extensions plug in their own
//! visualization. Visibility is filtered per type through `show`.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const render = @import("render");
const EditorState = @import("EditorState.zig");
const MeshBounds = @import("MeshBounds.zig");

const Vector3 = engine.Vector3;
const Matrix4 = engine.Matrix4;
const math = engine.math;
const Gizmos = engine.Gizmos;
const Camera = render.Camera;

pub const Mode = enum { translate, rotate, scale };
pub const Axis = enum { x, y, z };

/// 2D screen-space point (physical pixels).
const Vec2 = struct { x: f32, y: f32 };

/// Viewport rectangle in physical pixels (matches the rendered target and dvui
/// mouse-event coordinates). Decoupled from dvui's parameterized Rect types.
pub const Rect = struct { x: f32, y: f32, w: f32, h: f32 };

// ── Public state (driven by the viewport toolbar) ──────────────────────────────

/// Active manipulation mode for the selected object's transform.
pub var mode: Mode = .translate;
/// When true, drags snap to the increments below.
pub var snap_enabled: bool = false;
pub var snap_translate: f32 = 0.5;
pub var snap_rotate: f32 = 15.0;
pub var snap_scale: f32 = 0.25;

/// Per-type gizmo visibility filters.
pub const Visibility = struct {
    transform_gizmo: bool = true,
    cameras: bool = true,
    lights: bool = true,
    colliders: bool = true,
    custom: bool = true,
    icons: bool = true,
    labels: bool = false,
    grid: bool = true,
    selection: bool = true,
};
pub var show: Visibility = .{};

/// Set true on the frame the gizmo consumed a mouse-press, so the viewport
/// suppresses click-to-select / camera navigation for that click.
pub var captured_mouse: bool = false;

// ── Internals ──────────────────────────────────────────────────────────────────

var world: Gizmos = .{};
var overlay: Gizmos = .{};

var hover_axis: ?Axis = null;
var drag_axis: ?Axis = null;
var dragging: bool = false;
var drag_before: engine.SceneNode = undefined;
var drag_start_param: f32 = 0; // axis t at grab (translate/scale)
var drag_start_angle: f32 = 0; // screen angle at grab (rotate)

/// Per-frame mouse input forwarded by the viewport (natural coordinates).
pub const MouseInput = struct {
    pos: Vec2,
    inside: bool,
    left_pressed: bool,
    left_down: bool,
    left_released: bool,
};

// ── Custom gizmo registry (extension point, Unity-style) ────────────────────────
//
// Components/extensions register, by component type name:
//   • a `drawer`  — records lines/labels into the gizmo buffer, and/or
//   • an `icon`   — TVG bytes drawn as a billboard at the object's position, and
//   • a `layer`   — a named visibility group shown in the Gizmos menu.

/// A custom gizmo drawer for a component. Receives the buffer, the owning node,
/// and the component, and records lines/labels. Pure data — no GPU/GUI access.
pub const DrawerFn = *const fn (giz: *Gizmos, node: *const engine.SceneNode, comp: *const engine.Component) void;

const Registered = struct {
    name: []const u8,
    drawer: ?DrawerFn = null,
    /// TVG icon bytes (e.g. `gui.entypo.*`), or null for no billboard icon.
    icon: ?[]const u8 = null,
    /// Named visibility layer (free-form). Defaults to "Default".
    layer: []const u8 = "Default",
};
var registry: [64]Registered = undefined;
var registry_count: usize = 0;

fn entry(type_name: []const u8) *Registered {
    for (registry[0..registry_count]) |*r|
        if (std.mem.eql(u8, r.name, type_name)) return r;
    const r = &registry[@min(registry_count, registry.len - 1)];
    if (registry_count < registry.len) registry_count += 1;
    r.* = .{ .name = type_name };
    return r;
}

/// Register a custom gizmo drawer for a user-script component, keyed by its
/// type name (e.g. "WaypointPath"). Replaces any existing drawer for that name.
pub fn registerGizmo(type_name: []const u8, drawer: DrawerFn) void {
    entry(type_name).drawer = drawer;
}

/// Register a billboard icon (TVG bytes) for a user-script component type.
pub fn registerIcon(type_name: []const u8, icon: []const u8) void {
    entry(type_name).icon = icon;
}

/// Assign a named visibility layer to a component type's gizmos.
pub fn setLayer(type_name: []const u8, layer: []const u8) void {
    entry(type_name).layer = layer;
}

fn lookup(type_name: []const u8) ?*Registered {
    for (registry[0..registry_count]) |*r|
        if (std.mem.eql(u8, r.name, type_name)) return r;
    return null;
}

fn lookupDrawer(type_name: []const u8) ?DrawerFn {
    if (lookup(type_name)) |r| return r.drawer;
    return null;
}

fn lookupIcon(type_name: []const u8) ?[]const u8 {
    if (lookup(type_name)) |r| return r.icon;
    return null;
}

// ── Accessors for the renderer ──────────────────────────────────────────────────

pub fn worldVertices() []const Gizmos.Vertex {
    return world.vertices();
}
pub fn overlayVertices() []const Gizmos.Vertex {
    return overlay.vertices();
}
pub fn worldLabels() []const Gizmos.Label {
    return world.recordedLabels();
}

// ── Billboard icons ─────────────────────────────────────────────────────────────

/// A screen-space icon to draw over the viewport for one object. Coordinates are
/// relative to the viewport's top-left, in the units of the `rect` passed to
/// `collectIcons` (the caller passes a natural-coordinate rect).
pub const IconPlacement = struct {
    x: f32,
    y: f32,
    glyph: []const u8,
    color: Gizmos.Color,
    obj_index: usize,
};

/// Built-in icons for the standard light/camera components (TVG bytes).
const light_icon = gui.entypo.light_bulb;
const camera_icon = gui.entypo.video_camera;

/// Project each visible object's icon to screen and fill `out`. Returns the
/// count written. Lights and cameras get built-in icons; user-script components
/// use whatever icon was registered via `registerIcon`.
pub fn collectIcons(cam: Camera, rect: Rect, objects: []const engine.SceneNode, count: usize, out: []IconPlacement) usize {
    if (!show.icons) return 0;
    var n: usize = 0;
    for (objects[0..count], 0..) |*obj, i| {
        if (!obj.active or n >= out.len) continue;
        var glyph: ?[]const u8 = null;
        var color = Gizmos.Color.white;
        for (obj.components[0..obj.component_count]) |*comp| {
            switch (comp.*) {
                .light => |*lc| if (show.lights) {
                    glyph = light_icon;
                    color = Gizmos.Color.rgb(lc.color_r, lc.color_g, lc.color_b);
                },
                .camera => if (show.cameras) {
                    glyph = camera_icon;
                },
                .user_script => |*us| if (show.custom) {
                    if (lookupIcon(us.type_name[0..us.type_name_len])) |g| glyph = g;
                },
                else => {},
            }
            if (glyph != null) break;
        }
        const g = glyph orelse continue;
        const sp = worldToScreen(cam, rect, obj.transform.position) orelse continue;
        if (sp.x < 0 or sp.y < 0 or sp.x > rect.w or sp.y > rect.h) continue;
        out[n] = .{ .x = sp.x, .y = sp.y, .glyph = g, .color = color, .obj_index = i };
        n += 1;
    }
    return n;
}

// ── Main entry ──────────────────────────────────────────────────────────────────

/// Run interaction (picking / dragging the transform gizmo) and rebuild both
/// gizmo buffers for `objects` viewed through `cam` in `rect`. Call once per
/// frame from the viewport before rendering.
pub fn update(cam: Camera, rect: Rect, objects: []engine.SceneNode, count: usize, m: MouseInput) void {
    captured_mouse = false;
    const sel: ?usize = if (EditorState.selected_object) |s| (if (s < count) s else null) else null;

    interact(cam, rect, objects, sel, m);

    // A press that didn't grab a gizmo handle is a selection click: pick the
    // object under the cursor (or clear selection when clicking empty space).
    if (m.left_pressed and !captured_mouse and m.inside) {
        if (pickObject(cam, rect, objects, count, m.pos)) |hit| {
            EditorState.clearSelectedObjects();
            EditorState.selected_object = hit;
            EditorState.selectObject(hit);
        } else {
            EditorState.clearSelectedObjects();
            EditorState.selected_object = null;
        }
    }

    const sel2: ?usize = if (EditorState.selected_object) |s| (if (s < count) s else null) else null;
    buildWorld(cam, rect, objects, count);
    buildOverlay(cam, rect, objects, sel2);
}

/// Pick the front-most object under the cursor. Mesh objects are tested against
/// a world-axis-aligned box derived from their scale; other objects (lights,
/// cameras, empties) are selectable by clicking near their screen position.
fn pickObject(cam: Camera, rect: Rect, objects: []engine.SceneNode, count: usize, mouse: Vec2) ?usize {
    const ray = mouseRay(cam, rect, mouse);
    var best: ?usize = null;
    var best_t: f32 = 1e30;
    const ORIGIN_PICK_PX2: f32 = 16.0 * 16.0;
    for (objects[0..count], 0..) |*obj, i| {
        if (!obj.active) continue;
        var mesh_guid: []const u8 = "";
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* == .mesh_renderer) {
                mesh_guid = comp.mesh_renderer.mesh.slice();
                break;
            }
        }
        const t = &obj.transform;
        if (mesh_guid.len > 0) {
            // Prefer the cooked mesh's real bounds, transformed by the full
            // model matrix; fall back to a scale-sized box if not cooked yet.
            const wb = if (MeshBounds.local(mesh_guid)) |lb|
                transformAabb(modelMatrix(t), lb.min, lb.max)
            else box: {
                const half = Vector3{
                    .x = @max(@abs(t.scale.x) * 0.5, 0.05),
                    .y = @max(@abs(t.scale.y) * 0.5, 0.05),
                    .z = @max(@abs(t.scale.z) * 0.5, 0.05),
                };
                break :box MeshBounds.Bounds{ .min = t.position.subtract(half), .max = t.position.add(half) };
            };
            if (rayAabb(ray, wb.min, wb.max)) |th| {
                if (th < best_t) {
                    best_t = th;
                    best = i;
                }
            }
        } else if (worldToScreen(cam, rect, t.position)) |sp| {
            const dx = sp.x - mouse.x;
            const dy = sp.y - mouse.y;
            if (dx * dx + dy * dy <= ORIGIN_PICK_PX2) {
                const td = t.position.subtract(ray.o).dot(ray.d);
                if (td > 0 and td < best_t) {
                    best_t = td;
                    best = i;
                }
            }
        }
    }
    return best;
}

/// Full local-to-world transform for a node (translate · rotate · scale).
fn modelMatrix(t: *const engine.Transform) Matrix4 {
    return Matrix4.translation(t.position.x, t.position.y, t.position.z)
        .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
        .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
}

/// World-space AABB enclosing a local box transformed by `m` (all 8 corners).
fn transformAabb(m: Matrix4, lmin: Vector3, lmax: Vector3) MeshBounds.Bounds {
    const r = math.Geometry.transformAabb(m, lmin, lmax);
    return .{ .min = r.min, .max = r.max };
}

/// Ray vs. axis-aligned box (slab method). Returns the nearest positive entry
/// distance, or null if the ray misses.
fn rayAabb(ray: Ray, bmin: Vector3, bmax: Vector3) ?f32 {
    return math.Geometry.rayAabb(
        math.Ray{ .origin = ray.o, .direction = ray.d },
        bmin,
        bmax,
    );
}

fn interact(cam: Camera, rect: Rect, objects: []engine.SceneNode, sel: ?usize, m: MouseInput) void {
    const idx = sel orelse {
        dragging = false;
        drag_axis = null;
        hover_axis = null;
        return;
    };
    const node = &objects[idx];
    const origin = node.transform.position;
    const scale = gizmoScale(cam, origin, rect);

    if (dragging) {
        applyDrag(cam, rect, node, m.pos, scale);
        if (m.left_released) {
            // One undo entry per drag.
            EditorState.pushCommand(gui.frameTimeNS(), &.{ .modify_object = .{
                .idx = idx,
                .before = drag_before,
                .after = node.*,
            } });
            EditorState.scene_dirty = true;
            dragging = false;
            drag_axis = null;
        }
        captured_mouse = true;
        return;
    }

    hover_axis = if (m.inside) pickAxis(cam, rect, origin, scale, m.pos) else null;

    if (m.left_pressed and hover_axis != null) {
        drag_axis = hover_axis;
        dragging = true;
        drag_before = node.*;
        beginDrag(cam, rect, node, m.pos, scale);
        captured_mouse = true;
    }
}

// ── Drag math ────────────────────────────────────────────────────────────────

const Ray = struct { o: Vector3, d: Vector3 };

fn beginDrag(cam: Camera, rect: Rect, node: *engine.SceneNode, mouse: Vec2, scale: f32) void {
    const axis = drag_axis.?;
    const origin = node.transform.position;
    switch (mode) {
        .translate, .scale => {
            drag_start_param = axisParam(cam, rect, origin, axis, mouse) orelse 0;
        },
        .rotate => {
            drag_start_angle = screenAngle(cam, rect, origin, mouse) orelse 0;
        },
    }
    _ = scale;
}

fn applyDrag(cam: Camera, rect: Rect, node: *engine.SceneNode, mouse: Vec2, scale: f32) void {
    const axis = drag_axis.?;
    const origin = node.transform.position;
    _ = scale;
    switch (mode) {
        .translate => {
            const t = axisParam(cam, rect, origin, axis, mouse) orelse return;
            var delta = t - drag_start_param;
            if (snap_enabled) delta = snapTo(delta, snap_translate);
            const before = drag_before.transform.position;
            node.transform.position = before.add(axisVec(axis).scale(delta));
        },
        .scale => {
            const t = axisParam(cam, rect, origin, axis, mouse) orelse return;
            var delta = t - drag_start_param;
            if (snap_enabled) delta = snapTo(delta, snap_scale);
            var s = drag_before.transform.scale;
            switch (axis) {
                .x => s.x = @max(0.01, s.x + delta),
                .y => s.y = @max(0.01, s.y + delta),
                .z => s.z = @max(0.01, s.z + delta),
            }
            node.transform.scale = s;
        },
        .rotate => {
            const ang = screenAngle(cam, rect, origin, mouse) orelse return;
            var delta = (ang - drag_start_angle) * 180.0 / std.math.pi;
            if (snap_enabled) delta = snapTo(delta, snap_rotate);
            var r = drag_before.transform.rotation;
            switch (axis) {
                .x => r.x += delta,
                .y => r.y += delta,
                .z => r.z += delta,
            }
            node.transform.rotation = r;
        },
    }
}

/// Closest parameter `t` along the world axis line (origin + t·axis) to the ray
/// through the mouse. Used to slide translate/scale handles.
fn axisParam(cam: Camera, rect: Rect, origin: Vector3, axis: Axis, mouse: Vec2) ?f32 {
    const vp = [_]f32{ rect.x, rect.y, rect.w, rect.h };
    const ray = engine.Projection.viewportPointToRay(cam.view_proj.inverse(), vp, .{ mouse.x, mouse.y });
    const ad = axisVec(axis);
    const w0 = ray.origin.subtract(origin);
    const b = ray.direction.dot(ad);
    const d = ray.direction.dot(w0);
    const e = ad.dot(w0);
    const denom = 1.0 - b * b;
    if (@abs(denom) < 1e-4) return null;
    return (e - b * d) / denom;
}

/// Screen-space angle (radians) of the mouse around the projected `origin`.
fn screenAngle(cam: Camera, rect: Rect, origin: Vector3, mouse: Vec2) ?f32 {
    const c = worldToScreen(cam, rect, origin) orelse return null;
    return std.math.atan2(mouse.y - c.y, mouse.x - c.x);
}

fn snapTo(v: f32, step: f32) f32 {
    return math.Geometry.snapTo(v, step);
}

// ── Picking ────────────────────────────────────────────────────────────────────

// Transform-handle line thickness (screen-space pixels); the hovered/active
// axis is drawn fatter for feedback.
const HANDLE_WIDTH: f32 = 3.0;
const HANDLE_WIDTH_HOT: f32 = 6.0;
// Line handles (move/scale) get a generous grab radius; rotation rings are big
// targets already so they stay tighter.
const LINE_PICK_THRESHOLD: f32 = 16.0; // pixels
const RING_PICK_THRESHOLD: f32 = 9.0; // pixels
// Move/scale shafts start a fraction out from the origin so the three axes don't
// overlap into an ambiguous blob where they meet.
const SHAFT_INNER: f32 = 0.2;

/// Return the axis handle under the mouse, or null. For the move/scale line
/// handles this is camera-aware: when the click is near where two axes cross,
/// the more face-on (longer on screen, easier to drag) axis wins the tie.
fn pickAxis(cam: Camera, rect: Rect, origin: Vector3, scale: f32, mouse: Vec2) ?Axis {
    if (mode == .rotate) {
        var best: ?Axis = null;
        var best_d: f32 = RING_PICK_THRESHOLD;
        for ([_]Axis{ .x, .y, .z }) |axis| {
            const d = ringDistance(cam, rect, origin, axis, scale, mouse);
            if (d < best_d) {
                best_d = d;
                best = axis;
            }
        }
        return best;
    }

    var best: ?Axis = null;
    var best_d: f32 = LINE_PICK_THRESHOLD;
    var best_proj: f32 = 0;
    for ([_]Axis{ .x, .y, .z }) |axis| {
        const ad = axisVec(axis);
        const a = worldToScreen(cam, rect, origin.add(ad.scale(scale * SHAFT_INNER))) orelse continue;
        const b = worldToScreen(cam, rect, origin.add(ad.scale(scale))) orelse continue;
        const d = distToSegment(mouse, a, b);
        if (d >= LINE_PICK_THRESHOLD) continue;
        const proj = @sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
        // Clearly-closer axis wins; near-ties go to the longer (more face-on) one.
        const better = best == null or d < best_d - 5.0 or (@abs(d - best_d) <= 5.0 and proj > best_proj);
        if (better) {
            best = axis;
            best_d = d;
            best_proj = proj;
        }
    }
    return best;
}

/// Screen-space distance from the mouse to a rotation ring (sampled polyline).
fn ringDistance(cam: Camera, rect: Rect, origin: Vector3, axis: Axis, scale: f32, mouse: Vec2) f32 {
    const ad = axisVec(axis);
    var min_d: f32 = 1e9;
    var prev: ?Vec2 = null;
    const segs = 48;
    var i: usize = 0;
    while (i <= segs) : (i += 1) {
        const p = circlePoint(origin, ad, scale, i, segs);
        const sp = worldToScreen(cam, rect, p) orelse {
            prev = null;
            continue;
        };
        if (prev) |pv| min_d = @min(min_d, distToSegment(mouse, pv, sp));
        prev = sp;
    }
    return min_d;
}

// ── Buffer building ────────────────────────────────────────────────────────────

fn buildOverlay(cam: Camera, rect: Rect, objects: []engine.SceneNode, sel: ?usize) void {
    overlay.clear();
    if (!show.transform_gizmo) return;
    const idx = sel orelse return;
    const node = &objects[idx];
    const origin = node.transform.position;
    const scale = gizmoScale(cam, origin, rect);

    const active = if (dragging) drag_axis else hover_axis;
    for ([_]Axis{ .x, .y, .z }) |axis| {
        const hot = (active != null and active.? == axis);
        overlay.setColor(if (hot) Gizmos.Color.yellow else axisColor(axis));
        // Pixel-thick shafts so handles read (and grab) like solid bars rather
        // than 1px threads; the hovered/active axis swells for feedback.
        overlay.setLineWidth(if (hot) HANDLE_WIDTH_HOT else HANDLE_WIDTH);
        const ad = axisVec(axis);
        const tip = origin.add(ad.scale(scale));
        switch (mode) {
            .translate => {
                overlay.arrow(origin, tip);
                overlay.wireCone(tip, ad.negate(), 22, scale * 0.22);
            },
            .scale => {
                overlay.arrow(origin, tip);
                overlay.box(tip, Vector3.splat(scale * 0.14));
            },
            .rotate => overlay.circle(origin, ad, scale, 48),
        }
    }
    // Small center marker.
    overlay.setColor(Gizmos.Color.white);
    overlay.setLineWidth(HANDLE_WIDTH);
    overlay.box(origin, Vector3.splat(scale * 0.06));
}

fn buildWorld(cam: Camera, rect: Rect, objects: []engine.SceneNode, count: usize) void {
    world.clear();
    const aspect = rect.w / @max(rect.h, 1.0);

    if (show.grid) drawGrid();

    for (objects[0..count]) |*obj| {
        if (!obj.active) continue;
        const selected = EditorState.isObjectSelected(idxOf(objects, obj));
        for (obj.components[0..obj.component_count]) |*comp| {
            switch (comp.*) {
                // Camera and light visualizers are shown only for the selected
                // node — otherwise a populated scene fills the viewport with
                // overlapping frustums, rays and rings that read as clutter
                // (and you'd be standing inside the active camera's frustum).
                .camera => |*cc| if (show.cameras and selected) drawCameraGizmo(obj, cc, aspect),
                .light => |*lc| if (show.lights and selected) drawLightGizmo(obj, lc),
                .collider => if (show.colliders) drawColliderGizmo(obj),
                .user_script => |*us| if (show.custom) {
                    if (lookupDrawer(us.type_name[0..us.type_name_len])) |drawer|
                        drawer(&world, obj, comp);
                },
                else => {},
            }
        }
        if (selected and show.selection) drawSelectionBounds(obj);
        if (show.labels) {
            world.setColor(Gizmos.Color.white);
            world.label(obj.transform.position, obj.nameSlice());
        }
    }
    _ = cam;
}

fn idxOf(objects: []engine.SceneNode, obj: *engine.SceneNode) usize {
    const base = @intFromPtr(objects.ptr);
    const here = @intFromPtr(obj);
    return (here - base) / @sizeOf(engine.SceneNode);
}

fn drawGrid() void {
    const half: i32 = 10;
    const step: f32 = 1.0;
    world.setColor(Gizmos.Color.gray.withAlpha(0.35));
    var i: i32 = -half;
    while (i <= half) : (i += 1) {
        const f = @as(f32, @floatFromInt(i)) * step;
        const ext = @as(f32, @floatFromInt(half)) * step;
        // Brighten the center axes.
        if (i == 0) world.setColor(Gizmos.Color.gray.withAlpha(0.7)) else world.setColor(Gizmos.Color.gray.withAlpha(0.3));
        world.line(.{ .x = f, .y = 0, .z = -ext }, .{ .x = f, .y = 0, .z = ext });
        world.line(.{ .x = -ext, .y = 0, .z = f }, .{ .x = ext, .y = 0, .z = f });
    }
}

fn drawCameraGizmo(obj: *const engine.SceneNode, cc: *const engine.CameraComponent, aspect: f32) void {
    const t = &obj.transform;
    const rm = Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z);
    const fwd = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
    const look = t.position.add(fwd);
    const view = Matrix4.lookAt(t.position, look, .{ .x = 0, .y = 1, .z = 0 });
    // Draw a short frustum so it stays compact in the viewport.
    const far = @min(cc.far, cc.near + 4.0);
    const proj = Matrix4.perspective(cc.fov, aspect, cc.near, far);
    const inv = proj.multiply(view).inverse();
    world.setColor(Gizmos.Color.cyan);
    world.frustum(inv);
}

fn drawLightGizmo(obj: *const engine.SceneNode, lc: *const engine.LightComponent) void {
    const t = &obj.transform;
    const pos = t.position;
    const rm = Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z);
    const dir = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
    world.setColor(Gizmos.Color.rgb(lc.color_r, lc.color_g, lc.color_b));
    switch (lc.kind) {
        .directional => {
            // A small sun disc plus parallel rays along the light direction.
            world.circle(pos, dir, 0.3, 16);
            for ([_]Vector3{
                .{ .x = 0.3, .y = 0, .z = 0 }, .{ .x = -0.3, .y = 0, .z = 0 },
                .{ .x = 0, .y = 0.3, .z = 0 }, .{ .x = 0, .y = -0.3, .z = 0 },
            }) |off| {
                const s = pos.add(off);
                world.line(s, s.add(dir.scale(1.5)));
            }
        },
        .point => world.wireSphere(pos, lc.range),
        .spot => world.wireCone(pos, dir, lc.spot_angle, lc.range),
    }
}

fn drawColliderGizmo(obj: *const engine.SceneNode) void {
    const t = &obj.transform;
    world.setColor(Gizmos.Color.green.withAlpha(0.8));
    world.box(t.position, t.scale);
}

/// Outline the selected object's bounds. Only mesh nodes get a box — it wraps
/// the mesh's real AABB (transformed by the full model matrix), so it hugs the
/// geometry instead of guessing from scale. Immaterial nodes (lights, cameras,
/// empties) have no spatial extent, so they get no box; their billboard icon and
/// the transform handles already mark the selection.
fn drawSelectionBounds(obj: *const engine.SceneNode) void {
    var mesh_guid: []const u8 = "";
    for (obj.components[0..obj.component_count]) |*comp| {
        if (comp.* == .mesh_renderer) {
            mesh_guid = comp.mesh_renderer.mesh.slice();
            break;
        }
    }
    if (mesh_guid.len == 0) return;

    const t = &obj.transform;
    // Prefer the cooked mesh's real bounds; fall back to a scale-sized box until
    // it is cooked. Matches the picking volume in `pickObject`.
    const wb = if (MeshBounds.local(mesh_guid)) |lb|
        transformAabb(modelMatrix(t), lb.min, lb.max)
    else box: {
        const half = Vector3{
            .x = @max(@abs(t.scale.x) * 0.5, 0.05),
            .y = @max(@abs(t.scale.y) * 0.5, 0.05),
            .z = @max(@abs(t.scale.z) * 0.5, 0.05),
        };
        break :box MeshBounds.Bounds{ .min = t.position.subtract(half), .max = t.position.add(half) };
    };
    const center = wb.min.add(wb.max).scale(0.5);
    const size = wb.max.subtract(wb.min).scale(1.05);
    world.setColor(Gizmos.Color.orange);
    world.box(center, size);
}

// ── Geometry helpers ────────────────────────────────────────────────────────────

fn axisVec(a: Axis) Vector3 {
    return switch (a) {
        .x => .{ .x = 1, .y = 0, .z = 0 },
        .y => .{ .x = 0, .y = 1, .z = 0 },
        .z => .{ .x = 0, .y = 0, .z = 1 },
    };
}

fn axisColor(a: Axis) Gizmos.Color {
    return switch (a) {
        .x => Gizmos.Color.red,
        .y => Gizmos.Color.green,
        .z => Gizmos.Color.blue,
    };
}

fn circlePoint(center: Vector3, axis: Vector3, radius: f32, i: usize, segs: usize) Vector3 {
    return math.Geometry.circlePoint(center, axis, radius, i, segs);
}

/// Constant on-screen handle size: ~90px tall regardless of camera distance.
fn gizmoScale(cam: Camera, origin: Vector3, rect: Rect) f32 {
    const dist = @max(origin.subtract(cam.pos).length(), 0.01);
    return math.Projection.worldPerPixel(dist, cam.fov, rect.h) * 90.0;
}

// ── Projection ──────────────────────────────────────────────────────────────────

fn worldToScreen(cam: Camera, rect: Rect, w: Vector3) ?Vec2 {
    const vp = [_]f32{ rect.x, rect.y, rect.w, rect.h };
    // engine.Projection.worldToViewport (not math.Projection.worldToScreen
    // directly) — math-3d returns Y-up, but the viewport rect/mouse position
    // this is compared against is Y-down.
    const r = engine.Projection.worldToViewport(cam.view_proj, vp, w) orelse return null;
    return .{ .x = r[0], .y = r[1] };
}

fn mouseRay(cam: Camera, rect: Rect, m: Vec2) Ray {
    const vp = [_]f32{ rect.x, rect.y, rect.w, rect.h };
    const r = engine.Projection.viewportPointToRay(cam.view_proj.inverse(), vp, .{ m.x, m.y });
    return .{ .o = r.origin, .d = r.direction };
}

fn distToSegment(p: Vec2, a: Vec2, b: Vec2) f32 {
    return math.Geometry.distToSegment(p.x, p.y, a.x, a.y, b.x, b.y);
}

// ── Tests ──────────────────────────────────────────────────────────────────────

test "snapTo rounds to the nearest increment" {
    try std.testing.expectEqual(@as(f32, 1.5), snapTo(1.6, 0.5));
    try std.testing.expectEqual(@as(f32, 0.0), snapTo(0.2, 0.5));
    try std.testing.expectEqual(@as(f32, -1.0), snapTo(-1.1, 0.5));
    // Zero/negative step is a no-op.
    try std.testing.expectEqual(@as(f32, 3.3), snapTo(3.3, 0));
}

test "distToSegment measures perpendicular distance" {
    const d = distToSegment(.{ .x = 5, .y = 3 }, .{ .x = 0, .y = 0 }, .{ .x = 10, .y = 0 });
    try std.testing.expectApproxEqAbs(@as(f32, 3), d, 1e-4);
    // Past the segment end clamps to the endpoint.
    const d2 = distToSegment(.{ .x = 13, .y = 0 }, .{ .x = 0, .y = 0 }, .{ .x = 10, .y = 0 });
    try std.testing.expectApproxEqAbs(@as(f32, 3), d2, 1e-4);
}

fn testCamera(rect: Rect) Camera {
    const eye = Vector3{ .x = 0, .y = 0, .z = -5 };
    const view = Matrix4.lookAt(eye, .{ .x = 0, .y = 0, .z = 0 }, .{ .x = 0, .y = 1, .z = 0 });
    const proj = Matrix4.perspective(60.0, rect.w / rect.h, 0.1, 100.0);
    return .{
        .pos = eye,
        .rotation = .{},
        .fov = 60,
        .near = 0.1,
        .far = 100,
        .view = view,
        .proj = proj,
        .view_proj = proj.multiply(view),
    };
}

test "mouseRay through a projected point passes back through it" {
    const rect = Rect{ .x = 0, .y = 0, .w = 800, .h = 600 };
    const cam = testCamera(rect);
    const wp = Vector3{ .x = 1, .y = 0.5, .z = 0 };

    const sp = worldToScreen(cam, rect, wp) orelse return error.BehindCamera;
    try std.testing.expect(sp.x > 0 and sp.x < rect.w);
    try std.testing.expect(sp.y > 0 and sp.y < rect.h);

    const ray = mouseRay(cam, rect, sp);
    // Closest point on the ray to wp should be the point itself.
    const t = wp.subtract(ray.o).dot(ray.d);
    const closest = ray.o.add(ray.d.scale(t));
    try std.testing.expect(wp.subtract(closest).length() < 0.02);
}

test "rayAabb hits a box in front and misses one to the side" {
    const ray = Ray{ .o = .{ .x = 0, .y = 0, .z = -5 }, .d = .{ .x = 0, .y = 0, .z = 1 } };
    const hit = rayAabb(ray, .{ .x = -1, .y = -1, .z = -1 }, .{ .x = 1, .y = 1, .z = 1 });
    try std.testing.expect(hit != null);
    try std.testing.expectApproxEqAbs(@as(f32, 4), hit.?, 1e-3); // enters at z=-1
    const miss = rayAabb(ray, .{ .x = 5, .y = 5, .z = -1 }, .{ .x = 7, .y = 7, .z = 1 });
    try std.testing.expect(miss == null);
}

test "worldToScreen places higher points nearer the top" {
    const rect = Rect{ .x = 0, .y = 0, .w = 800, .h = 600 };
    const cam = testCamera(rect);
    const low = worldToScreen(cam, rect, .{ .x = 0, .y = -1, .z = 0 }) orelse return error.BehindCamera;
    const high = worldToScreen(cam, rect, .{ .x = 0, .y = 1, .z = 0 }) orelse return error.BehindCamera;
    // y-down (GUI/viewport convention): nearer the top means a *smaller* y.
    try std.testing.expect(high.y < low.y);
}
