//! Camera-orientation gizmo: a small overlay in the top-right corner of the
//! Scene View showing six cone handles radiating from a central cube hub,
//! rotated to match the editor camera's current orientation (Unity's "view
//! gizmo" widget). Each cone is a true 3D primitive — apex plus a sampled
//! base circle, projected and convex-hulled — so it always reads as a solid
//! shape instead of thinning to a sliver at certain view angles the way a
//! single flat triangle would. Clicking a handle snaps the camera to look
//! straight down that world axis while keeping the same focus point in
//! view. A persistent "< Persp" control resets to a default 3/4 view, since
//! axis-aligned views have no orbit to drag back out of on their own.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const EditorCamera = @import("EditorCamera.zig");
const GizmoSystem = @import("GizmoSystem.zig");

const Vector3 = engine.Vector3;
const Matrix4 = engine.Matrix4;

/// Margin from the viewport's top-right corner, in natural (unscaled)
/// pixels, and the widget's overall footprint used for laying out the
/// "< Persp" control.
const MARGIN: f32 = 28;
const FOOTPRINT: f32 = 76;
/// Cone length (hub to tip) and base radius, in natural pixels.
const ARM_LEN: f32 = 27;
const CONE_R: f32 = 7.0;
const CONE_R_NEG: f32 = 4.0;
/// Fraction of ARM_LEN where the cone's base circle sits, so the wide ends
/// tuck under the hub cube instead of meeting at a single point.
const BASE_INSET: f32 = 0.22;
/// Base-circle sample count for each cone (higher = rounder silhouette).
const CONE_SEGS = 8;
const MAX_HULL_PTS = CONE_SEGS + 1;
/// Hub cube half-extent.
const HUB_R: f32 = 7.0;
/// "< Persp" reset control, bottom-left of the widget footprint.
const PERSP_W: f32 = 60;
const PERSP_H: f32 = 22;
/// Snap animation duration, in seconds.
const DURATION: f32 = 0.4;
/// Focus distance used when no scene geometry is hit by the center ray.
const DEFAULT_FOCUS_DIST: f32 = 8.0;
/// Default 3/4 view the "< Persp" control resets to.
const PERSP_YAW: f32 = 45.0;
const PERSP_PITCH: f32 = -32.0;

const Vec2 = struct { x: f32, y: f32 };

const Face = struct {
    dir: Vector3,
    yaw: f32,
    pitch: f32,
    color: gui.Color,
    label: []const u8,
};

const RED = gui.Color{ .r = 210, .g = 80, .b = 65, .a = 255 };
const GREEN = gui.Color{ .r = 120, .g = 195, .b = 70, .a = 255 };
const BLUE = gui.Color{ .r = 70, .g = 130, .b = 210, .a = 255 };
const NEG_COLOR = gui.Color{ .r = 210, .g = 210, .b = 205, .a = 255 };
const EDGE_COLOR = gui.Color{ .r = 15, .g = 15, .b = 18, .a = 200 };
const HOVER_EDGE_COLOR = gui.Color{ .r = 255, .g = 240, .b = 130, .a = 255 };
const HUB_COLOR = gui.Color{ .r = 195, .g = 195, .b = 190, .a = 255 };

/// Fixed world-space light direction (doesn't rotate with the camera), so
/// the hub cube always reads as "lit from above" regardless of view angle.
const LIGHT_DIR = Vector3{ .x = 0.30, .y = 0.82, .z = 0.48 };

/// Target yaw/pitch for each axis were derived from `EditorCamera`'s forward
/// formula fwd = (-sin(yaw)*cos(pitch), sin(pitch), cos(yaw)*cos(pitch)).
const FACES = [_]Face{
    .{ .dir = .{ .x = 1, .y = 0, .z = 0 }, .yaw = -90, .pitch = 0, .color = RED, .label = "X" },
    .{ .dir = .{ .x = 0, .y = 1, .z = 0 }, .yaw = 0, .pitch = 90, .color = GREEN, .label = "Y" },
    .{ .dir = .{ .x = 0, .y = 0, .z = 1 }, .yaw = 0, .pitch = 0, .color = BLUE, .label = "Z" },
    .{ .dir = .{ .x = -1, .y = 0, .z = 0 }, .yaw = 90, .pitch = 0, .color = NEG_COLOR, .label = "" },
    .{ .dir = .{ .x = 0, .y = -1, .z = 0 }, .yaw = 0, .pitch = -90, .color = NEG_COLOR, .label = "" },
    .{ .dir = .{ .x = 0, .y = 0, .z = -1 }, .yaw = 180, .pitch = 0, .color = NEG_COLOR, .label = "" },
};

const CubeFace = struct { normal: Vector3, corners: [4]Vector3 };

/// The hub cube's 6 faces, each 4 corners on a +/-1 axis-aligned cube.
const CUBE_FACES = [_]CubeFace{
    .{ .normal = .{ .x = 1, .y = 0, .z = 0 }, .corners = .{
        .{ .x = 1, .y = -1, .z = -1 }, .{ .x = 1, .y = -1, .z = 1 },
        .{ .x = 1, .y = 1, .z = 1 },   .{ .x = 1, .y = 1, .z = -1 },
    } },
    .{ .normal = .{ .x = 0, .y = 1, .z = 0 }, .corners = .{
        .{ .x = -1, .y = 1, .z = -1 }, .{ .x = -1, .y = 1, .z = 1 },
        .{ .x = 1, .y = 1, .z = 1 },   .{ .x = 1, .y = 1, .z = -1 },
    } },
    .{ .normal = .{ .x = 0, .y = 0, .z = 1 }, .corners = .{
        .{ .x = -1, .y = -1, .z = 1 }, .{ .x = 1, .y = -1, .z = 1 },
        .{ .x = 1, .y = 1, .z = 1 },   .{ .x = -1, .y = 1, .z = 1 },
    } },
    .{ .normal = .{ .x = -1, .y = 0, .z = 0 }, .corners = .{
        .{ .x = -1, .y = -1, .z = 1 }, .{ .x = -1, .y = -1, .z = -1 },
        .{ .x = -1, .y = 1, .z = -1 }, .{ .x = -1, .y = 1, .z = 1 },
    } },
    .{ .normal = .{ .x = 0, .y = -1, .z = 0 }, .corners = .{
        .{ .x = -1, .y = -1, .z = 1 }, .{ .x = -1, .y = -1, .z = -1 },
        .{ .x = 1, .y = -1, .z = -1 }, .{ .x = 1, .y = -1, .z = 1 },
    } },
    .{ .normal = .{ .x = 0, .y = 0, .z = -1 }, .corners = .{
        .{ .x = 1, .y = -1, .z = -1 }, .{ .x = -1, .y = -1, .z = -1 },
        .{ .x = -1, .y = 1, .z = -1 }, .{ .x = 1, .y = 1, .z = -1 },
    } },
};

/// A clicked/hovered element of the widget.
pub const Hit = union(enum) {
    face: usize,
    perspective,
};

/// Per-Scene-instance snap animation state, persisted alongside the camera
/// pose in `SceneViewport.InstanceState`.
pub const Anim = struct {
    active: bool = false,
    t: f32 = 0,
    from_yaw: f32 = 0,
    from_pitch: f32 = 0,
    to_yaw: f32 = 0,
    to_pitch: f32 = 0,
    focus: Vector3 = .{},
    dist: f32 = DEFAULT_FOCUS_DIST,
};

const Basis = struct { right: Vector3, up: Vector3, fwd: Vector3 };

fn cameraBasis(yaw: f32, pitch: f32) Basis {
    const rm = Matrix4.rotationEuler(pitch, yaw, 0);
    return .{
        .right = rm.transformDirection(.{ .x = 1, .y = 0, .z = 0 }),
        .up = rm.transformDirection(.{ .x = 0, .y = 1, .z = 0 }),
        .fwd = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 }),
    };
}

fn lerp(a: f32, b: f32, t: f32) f32 {
    return a + (b - a) * t;
}

/// Symmetric ease: slow-in, fast-middle, slow-out — gentler at both ends
/// than a pure ease-out, so the snap doesn't feel like it "jumps" at t=0.
fn easeInOutCubic(t: f32) f32 {
    if (t < 0.5) return 4.0 * t * t * t;
    const f = -2.0 * t + 2.0;
    return 1.0 - (f * f * f) / 2.0;
}

/// Shortest-path target yaw equivalent to `target` (mod 360) starting from
/// `from`, so the camera doesn't spin the long way around after several
/// free-look turns have accumulated past +/-180.
fn shortestYaw(from: f32, target: f32) f32 {
    var delta = @mod(target - from + 180.0, 360.0) - 180.0;
    if (delta < -180.0) delta += 360.0;
    return from + delta;
}

/// Natural-space layout of the widget for a given viewport width.
const Layout = struct { cx: f32, cy: f32 };

fn layout(nat_w: f32) Layout {
    const half = FOOTPRINT / 2;
    return .{ .cx = nat_w - MARGIN - half, .cy = MARGIN + half };
}

fn perspRect(l: Layout) gui.Rect {
    const half = FOOTPRINT / 2;
    return .{ .x = l.cx - half, .y = l.cy + half - PERSP_H, .w = PERSP_W, .h = PERSP_H };
}

/// Project a world-space point (relative to the hub) to natural screen
/// coordinates.
fn projectPoint(b: Basis, l: Layout, w: Vector3) Vec2 {
    return .{
        .x = l.cx + w.dot(b.right),
        .y = l.cy - w.dot(b.up),
    };
}

/// Two unit vectors perpendicular to an axis-aligned `dir`, spanning the
/// plane a cone's base circle is sampled in.
fn perpsFor(dir: Vector3) struct { p1: Vector3, p2: Vector3 } {
    if (@abs(dir.x) > 0.5) return .{ .p1 = .{ .y = 1 }, .p2 = .{ .z = 1 } };
    if (@abs(dir.y) > 0.5) return .{ .p1 = .{ .x = 1 }, .p2 = .{ .z = 1 } };
    return .{ .p1 = .{ .x = 1 }, .p2 = .{ .y = 1 } };
}

fn cross(o: Vec2, a: Vec2, b: Vec2) f32 {
    return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
}

fn lessXY(_: void, a: Vec2, b: Vec2) bool {
    if (a.x != b.x) return a.x < b.x;
    return a.y < b.y;
}

fn halfHull(pts: []const Vec2, out: []Vec2) usize {
    var k: usize = 0;
    for (pts) |p| {
        while (k >= 2 and cross(out[k - 2], out[k - 1], p) <= 0) k -= 1;
        out[k] = p;
        k += 1;
    }
    return k;
}

/// 2D convex hull (Andrew's monotone chain) of `pts_in`, sorted in place.
/// Writes the hull (perimeter order) into `out` and returns its length.
fn convexHull(pts_in: []Vec2, out: []Vec2) usize {
    if (pts_in.len < 3) {
        for (pts_in, 0..) |p, i| out[i] = p;
        return pts_in.len;
    }
    std.sort.insertion(Vec2, pts_in, {}, lessXY);

    var lower: [MAX_HULL_PTS]Vec2 = undefined;
    const lower_n = halfHull(pts_in, &lower);

    var rev: [MAX_HULL_PTS]Vec2 = undefined;
    for (pts_in, 0..) |p, idx| rev[pts_in.len - 1 - idx] = p;
    var upper: [MAX_HULL_PTS]Vec2 = undefined;
    const upper_n = halfHull(rev[0..pts_in.len], &upper);

    var n: usize = 0;
    if (lower_n > 0) for (lower[0 .. lower_n - 1]) |p| {
        out[n] = p;
        n += 1;
    };
    if (upper_n > 0) for (upper[0 .. upper_n - 1]) |p| {
        out[n] = p;
        n += 1;
    };
    return n;
}

/// Builds the 2D silhouette (convex hull) of a 3D cone along `dir`: an apex
/// at `ARM_LEN` plus a sampled base circle of `base_r` near the hub. Always
/// yields a well-formed polygon from any view angle, unlike a flat triangle
/// with a fixed width axis (which thins to a sliver edge-on).
fn buildCone(b: Basis, l: Layout, dir: Vector3, base_r: f32, out: *[MAX_HULL_PTS]Vec2) usize {
    const perps = perpsFor(dir);
    const base_center = dir.scale(ARM_LEN * BASE_INSET);

    var pts: [MAX_HULL_PTS]Vec2 = undefined;
    pts[0] = projectPoint(b, l, dir.scale(ARM_LEN));
    for (0..CONE_SEGS) |i| {
        const angle = @as(f32, @floatFromInt(i)) * (2.0 * std.math.pi / @as(f32, CONE_SEGS));
        const offset = perps.p1.scale(base_r * @cos(angle)).add(perps.p2.scale(base_r * @sin(angle)));
        pts[1 + i] = projectPoint(b, l, base_center.add(offset));
    }
    return convexHull(&pts, out);
}

/// True if `p` is inside convex polygon `poly` (any winding).
fn pointInConvexPoly(p: Vec2, poly: []const Vec2) bool {
    if (poly.len < 3) return false;
    var saw_pos = false;
    var saw_neg = false;
    for (0..poly.len) |i| {
        const a = poly[i];
        const b2 = poly[(i + 1) % poly.len];
        const cr = (b2.x - a.x) * (p.y - a.y) - (b2.y - a.y) * (p.x - a.x);
        if (cr > 0) saw_pos = true;
        if (cr < 0) saw_neg = true;
    }
    return !(saw_pos and saw_neg);
}

fn hitTestLocal(nat_w: f32, px: f32, py: f32) ?Hit {
    const l = layout(nat_w);
    const pr = perspRect(l);
    if (px >= pr.x and px <= pr.x + pr.w and py >= pr.y and py <= pr.y + pr.h) return .perspective;

    const st = EditorCamera.getState();
    const b = cameraBasis(st.yaw, st.pitch);
    const p = Vec2{ .x = px, .y = py };

    var best: ?usize = null;
    var best_z: f32 = 1e30;
    for (FACES, 0..) |face, i| {
        const radius: f32 = if (face.label.len > 0) CONE_R else CONE_R_NEG;
        var hull: [MAX_HULL_PTS]Vec2 = undefined;
        const n = buildCone(b, l, face.dir, radius, &hull);
        const z = face.dir.dot(b.fwd);
        if (pointInConvexPoly(p, hull[0..n]) and z < best_z) {
            best_z = z;
            best = i;
        }
    }
    if (best) |idx| return .{ .face = idx };
    return null;
}

/// Hit-test a click in window-physical coordinates against the widget.
/// `nat_rect` is the Scene viewport content box's natural rect for this
/// frame, `phys`/`scale` its physical rect and `gui.windowNaturalScale()`.
pub fn pick(nat_rect: gui.Rect, phys: gui.Rect.Physical, scale: f32, mx: f32, my: f32) ?Hit {
    if (scale <= 0) return null;
    const px = (mx - phys.x) / scale;
    const py = (my - phys.y) / scale;
    return hitTestLocal(nat_rect.w, px, py);
}

fn focusPointAhead(pos: Vector3, fwd: Vector3, objects: []engine.SceneNode, count: usize) struct { focus: Vector3, dist: f32 } {
    var dist: f32 = DEFAULT_FOCUS_DIST;
    if (GizmoSystem.raycastNearest(pos, fwd, objects, count)) |t| {
        dist = std.math.clamp(t, 1.0, 500.0);
    } else if (@abs(fwd.y) > 1e-4) {
        const t = -pos.y / fwd.y;
        if (t > 0.5) dist = std.math.clamp(t, 1.0, 500.0);
    }
    return .{ .focus = pos.add(fwd.scale(dist)), .dist = dist };
}

fn beginAnim(anim: *Anim, to_yaw: f32, to_pitch: f32, objects: []engine.SceneNode, count: usize) void {
    const st = EditorCamera.getState();
    const b = cameraBasis(st.yaw, st.pitch);
    const fp = focusPointAhead(st.pos, b.fwd, objects, count);

    anim.* = .{
        .active = true,
        .t = 0,
        .from_yaw = st.yaw,
        .from_pitch = st.pitch,
        .to_yaw = shortestYaw(st.yaw, to_yaw),
        .to_pitch = to_pitch,
        .focus = fp.focus,
        .dist = fp.dist,
    };
}

fn tick(anim: *Anim, dt: f32) void {
    if (!anim.active) return;
    anim.t += if (DURATION > 0) dt / DURATION else 1.0;
    if (anim.t >= 1.0) {
        anim.t = 1.0;
        anim.active = false;
    }
    const e = easeInOutCubic(anim.t);
    EditorCamera.snapTo(
        lerp(anim.from_yaw, anim.to_yaw, e),
        lerp(anim.from_pitch, anim.to_pitch, e),
        anim.focus,
        anim.dist,
    );
}

fn scaleColor(c: gui.Color, factor: f32) gui.Color {
    const f = std.math.clamp(factor, 0.0, 1.6);
    return .{
        .r = @intFromFloat(std.math.clamp(@as(f32, @floatFromInt(c.r)) * f, 0, 255)),
        .g = @intFromFloat(std.math.clamp(@as(f32, @floatFromInt(c.g)) * f, 0, 255)),
        .b = @intFromFloat(std.math.clamp(@as(f32, @floatFromInt(c.b)) * f, 0, 255)),
        .a = c.a,
    };
}

fn toPhysical(phys: gui.Rect.Physical, scale: f32, p: Vec2) gui.Point.Physical {
    return .{ .x = phys.x + p.x * scale, .y = phys.y + p.y * scale };
}

fn drawPoly(alloc: std.mem.Allocator, phys: gui.Rect.Physical, scale: f32, poly: []const Vec2, fill: gui.Color, edge: gui.Color, edge_w: f32) void {
    var pb: gui.Path.Builder = .init(alloc);
    defer pb.deinit();
    for (poly) |p| pb.addPoint(toPhysical(phys, scale, p));
    pb.build().fillConvex(.{ .color = fill, .fade = 1.0 });
    pb.build().stroke(.{ .thickness = edge_w * scale, .color = edge, .closed = true });
}

const ZCtx = struct {
    fn lessThan(z: [FACES.len]f32, a: usize, c: usize) bool {
        return z[a] > z[c]; // farther (more positive z) drawn first
    }
};

fn drawHubCube(b: Basis, l: Layout, phys: gui.Rect.Physical, scale: f32, alloc: std.mem.Allocator) void {
    const CubeOrder = struct {
        fn lessThan(z: [CUBE_FACES.len]f32, a: usize, c: usize) bool {
            return z[a] > z[c];
        }
    };
    var order: [CUBE_FACES.len]usize = .{ 0, 1, 2, 3, 4, 5 };
    var zs: [CUBE_FACES.len]f32 = undefined;
    for (CUBE_FACES, 0..) |cf, i| zs[i] = cf.normal.dot(b.fwd);
    std.sort.insertion(usize, &order, zs, CubeOrder.lessThan);

    for (order) |i| {
        const cf = CUBE_FACES[i];
        if (zs[i] >= -1e-4) continue;
        var poly: [4]Vec2 = undefined;
        for (cf.corners, 0..) |c, k| poly[k] = projectPoint(b, l, c.scale(HUB_R));
        const light = @max(0.0, cf.normal.dot(LIGHT_DIR));
        const col = scaleColor(HUB_COLOR, 0.65 + 0.35 * light);
        drawPoly(alloc, phys, scale, &poly, col, EDGE_COLOR, 1.0);
    }
}

fn render(nat_rect: gui.Rect, phys: gui.Rect.Physical, scale: f32, hover: ?Hit) void {
    const st = EditorCamera.getState();
    const b = cameraBasis(st.yaw, st.pitch);
    const l = layout(nat_rect.w);
    const alloc = gui.currentWindow().lifo();

    var order: [FACES.len]usize = .{ 0, 1, 2, 3, 4, 5 };
    var zs: [FACES.len]f32 = undefined;
    for (FACES, 0..) |face, i| zs[i] = face.dir.dot(b.fwd);
    std.sort.insertion(usize, &order, zs, ZCtx.lessThan);

    for (order) |i| {
        const face = FACES[i];
        const is_hover = if (hover) |h| (h == .face and h.face == i) else false;
        const radius: f32 = if (face.label.len > 0) CONE_R else CONE_R_NEG;
        var hull: [MAX_HULL_PTS]Vec2 = undefined;
        const n = buildCone(b, l, face.dir, radius, &hull);
        const col = if (is_hover) scaleColor(face.color, 1.25) else face.color;
        const edge_w: f32 = if (is_hover) 2.0 else 1.0;
        drawPoly(alloc, phys, scale, hull[0..n], col, if (is_hover) HOVER_EDGE_COLOR else EDGE_COLOR, edge_w);

        if (face.label.len > 0) {
            const label_pos = projectPoint(b, l, face.dir.scale(ARM_LEN + 13));
            gui.label(@src(), "{s}", .{face.label}, .{
                .id_extra = i,
                .rect = .{ .x = label_pos.x - 14, .y = label_pos.y - 12, .w = 28, .h = 24 },
                .gravity_x = 0.5,
                .gravity_y = 0.5,
                .color_text = .{ .r = 235, .g = 235, .b = 235, .a = 255 },
            });
        }
    }

    // Hub cube, drawn last so the cone bases tuck cleanly underneath it.
    drawHubCube(b, l, phys, scale, alloc);

    const pr = perspRect(l);
    const persp_hover = if (hover) |h| h == .perspective else false;
    gui.label(@src(), "< Persp", .{}, .{
        .rect = pr,
        .gravity_x = 0.0,
        .gravity_y = 0.5,
        .color_text = if (persp_hover) gui.Color{ .r = 255, .g = 255, .b = 255, .a = 255 } else gui.Color{ .r = 190, .g = 190, .b = 190, .a = 210 },
    });
}

/// Apply this frame's click (if any) and advance the snap animation. Called
/// before the scene is rendered so the camera pose used for this frame's
/// image already reflects the animation step (kept separate from `draw` to
/// avoid a one-frame lag between the gizmo and the 3D view).
pub fn applySnap(hit: ?Hit, clicked: bool, objects: []engine.SceneNode, count: usize, anim: *Anim) void {
    if (clicked) {
        if (hit) |h| switch (h) {
            .face => |idx| beginAnim(anim, FACES[idx].yaw, FACES[idx].pitch, objects, count),
            .perspective => beginAnim(anim, PERSP_YAW, PERSP_PITCH, objects, count),
        };
    }
    tick(anim, gui.secondsSinceLastFrame());
}

/// Draw the corner widget for this frame, called from the Scene View overlay
/// after the rendered image. `hover` highlights the element under the
/// cursor.
pub fn draw(nat_rect: gui.Rect, phys: gui.Rect.Physical, scale: f32, hover: ?Hit) void {
    render(nat_rect, phys, scale, hover);
}
