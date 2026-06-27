const std = @import("std");
const math = @import("math");
const Vector3 = math.Vector3;
const Matrix4 = math.Matrix4;

/// Immediate-mode debug-draw command buffer (issue #3).
///
/// `Gizmos` is **pure data** — it records colored line segments and text labels
/// into fixed-capacity buffers and does no rendering itself. The editor's GPU
/// renderer consumes the recorded primitives each frame. Because the type has no
/// dependency on the GUI/GPU layers, user script components (which link only the
/// engine) can fill a `Gizmos` buffer in their own `drawGizmos` method, giving
/// components and extensions a way to register custom gizmos.
///
/// Every higher-level shape (`box`, `wireSphere`, `cone`, …) decomposes into the
/// same colored line primitive, so the renderer only ever needs to draw a flat
/// list of `Vertex` pairs as a line list, plus the labels.
///
/// Typical use (per frame):
/// ```zig
/// gizmos.clear();
/// gizmos.setColor(.green);
/// gizmos.wireSphere(node.transform.position, 0.5);
/// gizmos.label(node.transform.position, node.nameSlice());
/// ```
pub const Gizmos = struct {
    /// Maximum number of line endpoints (each line uses two). Sized generously so
    /// a full scene of wire shapes plus the transform gizmo fits comfortably.
    pub const MAX_VERTICES = 1 << 15;
    /// Maximum number of text labels per frame.
    pub const MAX_LABELS = 256;
    /// Maximum byte length of a single label.
    pub const LABEL_TEXT_MAX = 64;
    /// Default number of segments used to approximate a circle.
    pub const CIRCLE_SEGMENTS = 32;

    /// Linear RGBA color, components in 0..1.
    pub const Color = extern struct {
        r: f32 = 1,
        g: f32 = 1,
        b: f32 = 1,
        a: f32 = 1,

        pub const white = Color{ .r = 1, .g = 1, .b = 1 };
        pub const black = Color{ .r = 0, .g = 0, .b = 0 };
        pub const gray = Color{ .r = 0.5, .g = 0.5, .b = 0.5 };
        pub const red = Color{ .r = 0.9, .g = 0.2, .b = 0.2 };
        pub const green = Color{ .r = 0.3, .g = 0.85, .b = 0.3 };
        pub const blue = Color{ .r = 0.3, .g = 0.5, .b = 0.95 };
        pub const yellow = Color{ .r = 0.95, .g = 0.85, .b = 0.2 };
        pub const cyan = Color{ .r = 0.2, .g = 0.85, .b = 0.9 };
        pub const magenta = Color{ .r = 0.9, .g = 0.3, .b = 0.85 };
        pub const orange = Color{ .r = 0.95, .g = 0.55, .b = 0.15 };

        pub fn rgb(r: f32, g: f32, b: f32) Color {
            return .{ .r = r, .g = g, .b = b, .a = 1 };
        }

        pub fn withAlpha(self: Color, a: f32) Color {
            return .{ .r = self.r, .g = self.g, .b = self.b, .a = a };
        }
    };

    /// One line endpoint: position, color, and screen-space `thickness` in
    /// pixels. The renderer expands each segment into a camera-facing quad, so
    /// thickness is per-vertex — a segment can taper (comet/ribbon) by giving
    /// its two endpoints different widths.
    pub const Vertex = extern struct {
        pos: [3]f32,
        color: [4]f32,
        thickness: f32 = 1,
    };

    /// A world-space text label.
    pub const Label = struct {
        pos: [3]f32,
        color: Color,
        text: [LABEL_TEXT_MAX]u8 = undefined,
        len: usize = 0,

        pub fn slice(self: *const Label) []const u8 {
            return self.text[0..self.len];
        }
    };

    verts: [MAX_VERTICES]Vertex = undefined,
    vert_count: usize = 0,
    labels: [MAX_LABELS]Label = undefined,
    label_count: usize = 0,

    /// Current draw color, applied to every subsequent primitive.
    color: Color = Color.white,
    /// Current line thickness (screen-space pixels), applied to every subsequent
    /// vertex. Use `lineW`/`ribbon` to vary thickness per endpoint instead.
    line_width: f32 = 1,
    /// Optional transform applied to every position before recording. Mirrors
    /// Unity's `Gizmos.matrix`; lets a component draw in its own local space.
    matrix: Matrix4 = Matrix4{},
    use_matrix: bool = false,

    /// Reset the buffer for a new frame.
    pub fn clear(self: *Gizmos) void {
        self.vert_count = 0;
        self.label_count = 0;
        self.color = Color.white;
        self.line_width = 1;
        self.use_matrix = false;
    }

    /// Set the color used by subsequent primitives.
    pub fn setColor(self: *Gizmos, c: Color) void {
        self.color = c;
    }

    /// Set the line thickness (screen-space pixels) used by subsequent vertices.
    pub fn setLineWidth(self: *Gizmos, w: f32) void {
        self.line_width = w;
    }

    /// Apply `m` to all subsequent positions until `clearMatrix`.
    pub fn setMatrix(self: *Gizmos, m: Matrix4) void {
        self.matrix = m;
        self.use_matrix = true;
    }

    /// Stop applying the local matrix; positions are taken as world space.
    pub fn clearMatrix(self: *Gizmos) void {
        self.use_matrix = false;
    }

    /// Read-only view of the recorded line vertices (pairs form line segments).
    pub fn vertices(self: *const Gizmos) []const Vertex {
        return self.verts[0..self.vert_count];
    }

    /// Read-only view of the recorded labels.
    pub fn recordedLabels(self: *const Gizmos) []const Label {
        return self.labels[0..self.label_count];
    }

    // ── Core primitive ──────────────────────────────────────────────────────

    fn pushVertex(self: *Gizmos, p: Vector3) void {
        self.pushVertexT(p, self.line_width);
    }

    fn pushVertexT(self: *Gizmos, p: Vector3, thickness: f32) void {
        if (self.vert_count >= MAX_VERTICES) return;
        const w = if (self.use_matrix) self.matrix.transformPoint(p) else p;
        self.verts[self.vert_count] = .{
            .pos = .{ w.x, w.y, w.z },
            .color = .{ self.color.r, self.color.g, self.color.b, self.color.a },
            .thickness = thickness,
        };
        self.vert_count += 1;
    }

    /// Draw a single line segment between two points at the current line width.
    pub fn line(self: *Gizmos, a: Vector3, b: Vector3) void {
        // Keep endpoints paired: drop the segment entirely if it cannot fit.
        if (self.vert_count + 2 > MAX_VERTICES) return;
        self.pushVertex(a);
        self.pushVertex(b);
    }

    /// Draw a segment whose width tapers from `wa` (at `a`) to `wb` (at `b`),
    /// independent of the current `line_width`. Building block for ribbons.
    pub fn lineW(self: *Gizmos, a: Vector3, b: Vector3, wa: f32, wb: f32) void {
        if (self.vert_count + 2 > MAX_VERTICES) return;
        self.pushVertexT(a, wa);
        self.pushVertexT(b, wb);
    }

    /// Draw a connected variable-width ribbon through `points`, where `widths[i]`
    /// is the thickness at `points[i]` — e.g. a comet trail that tapers to a
    /// point. `widths` must have the same length as `points`; shorter input is
    /// ignored.
    pub fn ribbon(self: *Gizmos, points: []const Vector3, widths: []const f32) void {
        if (points.len < 2 or widths.len < points.len) return;
        var i: usize = 0;
        while (i + 1 < points.len) : (i += 1)
            self.lineW(points[i], points[i + 1], widths[i], widths[i + 1]);
    }

    /// Draw a ray from `origin` extending along `dir` (not normalized).
    pub fn ray(self: *Gizmos, origin: Vector3, dir: Vector3) void {
        self.line(origin, origin.add(dir));
    }

    /// Draw a connected polyline through `points`. With `closed`, the last point
    /// connects back to the first.
    pub fn polyline(self: *Gizmos, points: []const Vector3, closed: bool) void {
        if (points.len < 2) return;
        var i: usize = 0;
        while (i + 1 < points.len) : (i += 1) self.line(points[i], points[i + 1]);
        if (closed) self.line(points[points.len - 1], points[0]);
    }

    /// Draw an arrow from `from` to `to` with a small conical head.
    pub fn arrow(self: *Gizmos, from: Vector3, to: Vector3) void {
        self.line(from, to);
        const dir = to.subtract(from);
        const len = dir.length();
        if (len < 1e-5) return;
        self.arrowHead(to, dir.scale(1.0 / len), len);
    }

    /// Four head spokes for an arrow tipped at `to`, pointing along unit `d`,
    /// sized relative to the shaft `len`.
    fn arrowHead(self: *Gizmos, to: Vector3, d: Vector3, len: f32) void {
        const head = @max(len * 0.15, 1e-4);
        // Pick any axis not parallel to the direction for the head spokes.
        const ref = if (@abs(d.y) > 0.95) Vector3.right() else Vector3.up();
        const side = d.cross(ref).normalize();
        const up = d.cross(side).normalize();
        const base = to.subtract(d.scale(head));
        const s = head * 0.5;
        self.line(to, base.add(side.scale(s)));
        self.line(to, base.subtract(side.scale(s)));
        self.line(to, base.add(up.scale(s)));
        self.line(to, base.subtract(up.scale(s)));
    }

    /// Draw a hollow tube (cylinder wireframe) between `a` and `b`: `sides`
    /// parallel edges plus end caps. Used to give axis handles visible
    /// thickness so they read — and grab — like solid bars rather than threads.
    pub fn tube(self: *Gizmos, a: Vector3, b: Vector3, radius: f32, sides: usize) void {
        const dir = b.subtract(a);
        const len = dir.length();
        if (len < 1e-6) return;
        const d = dir.scale(1.0 / len);
        const ref = if (@abs(d.y) > 0.95) Vector3.right() else Vector3.up();
        const u = d.cross(ref).normalize();
        const v = d.cross(u).normalize();
        const s = @max(sides, 3);
        var i: usize = 0;
        while (i < s) : (i += 1) {
            const ang = @as(f32, @floatFromInt(i)) / @as(f32, @floatFromInt(s)) * std.math.tau;
            const off = u.scale(radius * @cos(ang)).add(v.scale(radius * @sin(ang)));
            self.line(a.add(off), b.add(off));
        }
        self.circle(a, d, radius, s);
        self.circle(b, d, radius, s);
    }

    // ── Boxes ───────────────────────────────────────────────────────────────

    /// Draw an axis-aligned wire box centered at `center` with full `size`.
    pub fn box(self: *Gizmos, center: Vector3, size: Vector3) void {
        const h = size.scale(0.5);
        const c = center;
        // 8 corners.
        const corners = [8]Vector3{
            .{ .x = c.x - h.x, .y = c.y - h.y, .z = c.z - h.z },
            .{ .x = c.x + h.x, .y = c.y - h.y, .z = c.z - h.z },
            .{ .x = c.x + h.x, .y = c.y + h.y, .z = c.z - h.z },
            .{ .x = c.x - h.x, .y = c.y + h.y, .z = c.z - h.z },
            .{ .x = c.x - h.x, .y = c.y - h.y, .z = c.z + h.z },
            .{ .x = c.x + h.x, .y = c.y - h.y, .z = c.z + h.z },
            .{ .x = c.x + h.x, .y = c.y + h.y, .z = c.z + h.z },
            .{ .x = c.x - h.x, .y = c.y + h.y, .z = c.z + h.z },
        };
        // bottom ring, top ring, verticals
        const edges = [12][2]usize{
            .{ 0, 1 }, .{ 1, 2 }, .{ 2, 3 }, .{ 3, 0 },
            .{ 4, 5 }, .{ 5, 6 }, .{ 6, 7 }, .{ 7, 4 },
            .{ 0, 4 }, .{ 1, 5 }, .{ 2, 6 }, .{ 3, 7 },
        };
        for (edges) |e| self.line(corners[e[0]], corners[e[1]]);
    }

    /// Draw a wire box for the unit cube (±0.5) transformed by `m`. Useful for
    /// drawing an oriented bounding box via a model matrix.
    pub fn wireCube(self: *Gizmos, m: Matrix4) void {
        const prev = self.matrix;
        const had = self.use_matrix;
        self.setMatrix(if (had) prev.multiply(m) else m);
        self.box(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 1, .y = 1, .z = 1 });
        if (had) self.setMatrix(prev) else self.clearMatrix();
    }

    // ── Circles & spheres ─────────────────────────────────────────────────────

    /// Draw a circle of `radius` centered at `center` lying in the plane whose
    /// normal is `axis`, approximated with `segments` line segments.
    pub fn circle(self: *Gizmos, center: Vector3, axis: Vector3, radius: f32, segments: usize) void {
        const n = axis.normalizeEps(1e-6);
        // Build an orthonormal basis (u, v) spanning the circle's plane.
        const ref = if (@abs(n.y) > 0.95) Vector3.right() else Vector3.up();
        const u = n.cross(ref).normalize();
        const v = n.cross(u).normalize();
        const seg = @max(segments, 3);
        var prev: Vector3 = undefined;
        var i: usize = 0;
        while (i <= seg) : (i += 1) {
            const t = @as(f32, @floatFromInt(i)) / @as(f32, @floatFromInt(seg)) * std.math.tau;
            const p = center
                .add(u.scale(radius * @cos(t)))
                .add(v.scale(radius * @sin(t)));
            if (i > 0) self.line(prev, p);
            prev = p;
        }
    }

    /// Draw a wireframe sphere as three orthogonal great circles.
    pub fn wireSphere(self: *Gizmos, center: Vector3, radius: f32) void {
        self.circle(center, Vector3.right(), radius, CIRCLE_SEGMENTS);
        self.circle(center, Vector3.up(), radius, CIRCLE_SEGMENTS);
        self.circle(center, Vector3.forward(), radius, CIRCLE_SEGMENTS);
    }

    /// Draw a wireframe cone: apex at `apex`, opening along `dir` (normalized
    /// internally), with `half_angle_deg` half-angle and the given slant `length`.
    pub fn wireCone(self: *Gizmos, apex: Vector3, dir: Vector3, half_angle_deg: f32, length: f32) void {
        const d = dir.normalizeEps(1e-6);
        const ang = half_angle_deg * std.math.pi / 180.0;
        const base_center = apex.add(d.scale(length * @cos(ang)));
        const base_radius = length * @sin(ang);
        self.circle(base_center, d, base_radius, CIRCLE_SEGMENTS);
        // Four spokes from apex to the base ring.
        const ref = if (@abs(d.y) > 0.95) Vector3.right() else Vector3.up();
        const u = d.cross(ref).normalize();
        const v = d.cross(u).normalize();
        const spokes = [4]Vector3{ u, v, u.negate(), v.negate() };
        for (spokes) |s| self.line(apex, base_center.add(s.scale(base_radius)));
    }

    /// Draw the 12 edges of a view frustum given the inverse of its
    /// view-projection matrix (maps NDC cube corners back to world space).
    pub fn frustum(self: *Gizmos, inv_view_proj: Matrix4) void {
        // NDC cube corners (Vulkan-style z in [0,1]).
        const ndc = [8]Vector3{
            .{ .x = -1, .y = -1, .z = 0 }, .{ .x = 1, .y = -1, .z = 0 },
            .{ .x = 1, .y = 1, .z = 0 },   .{ .x = -1, .y = 1, .z = 0 },
            .{ .x = -1, .y = -1, .z = 1 }, .{ .x = 1, .y = -1, .z = 1 },
            .{ .x = 1, .y = 1, .z = 1 },   .{ .x = -1, .y = 1, .z = 1 },
        };
        var corners: [8]Vector3 = undefined;
        for (ndc, 0..) |p, i| {
            const v = inv_view_proj.transformVector4(.{ .x = p.x, .y = p.y, .z = p.z, .w = 1 });
            const iw = if (@abs(v.w) > 1e-9) 1.0 / v.w else 0.0;
            corners[i] = .{ .x = v.x * iw, .y = v.y * iw, .z = v.z * iw };
        }
        const edges = [12][2]usize{
            .{ 0, 1 }, .{ 1, 2 }, .{ 2, 3 }, .{ 3, 0 },
            .{ 4, 5 }, .{ 5, 6 }, .{ 6, 7 }, .{ 7, 4 },
            .{ 0, 4 }, .{ 1, 5 }, .{ 2, 6 }, .{ 3, 7 },
        };
        for (edges) |e| self.line(corners[e[0]], corners[e[1]]);
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    /// Record a world-space text label using the current color.
    pub fn label(self: *Gizmos, pos: Vector3, text: []const u8) void {
        if (self.label_count >= MAX_LABELS) return;
        const w = if (self.use_matrix) self.matrix.transformPoint(pos) else pos;
        var l = &self.labels[self.label_count];
        l.pos = .{ w.x, w.y, w.z };
        l.color = self.color;
        l.len = @min(text.len, LABEL_TEXT_MAX);
        @memcpy(l.text[0..l.len], text[0..l.len]);
        self.label_count += 1;
    }
};

// ── Tests ──────────────────────────────────────────────────────────────────────

test "line records a pair of vertices" {
    var g = Gizmos{};
    g.clear();
    g.setColor(.red);
    g.line(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 1, .y = 2, .z = 3 });
    try std.testing.expectEqual(@as(usize, 2), g.vert_count);
    try std.testing.expectEqual(@as(f32, 1), g.verts[1].pos[0]);
    try std.testing.expectEqual(@as(f32, 0.9), g.verts[0].color[0]);
}

test "box records 12 edges" {
    var g = Gizmos{};
    g.clear();
    g.box(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 2, .y = 2, .z = 2 });
    // 12 edges × 2 endpoints.
    try std.testing.expectEqual(@as(usize, 24), g.vert_count);
}

test "matrix transforms recorded positions" {
    var g = Gizmos{};
    g.clear();
    g.setMatrix(Matrix4.translation(10, 0, 0));
    g.line(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 1, .y = 0, .z = 0 });
    try std.testing.expectEqual(@as(f32, 10), g.verts[0].pos[0]);
    try std.testing.expectEqual(@as(f32, 11), g.verts[1].pos[0]);
    g.clearMatrix();
    g.line(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 1, .y = 0, .z = 0 });
    try std.testing.expectEqual(@as(f32, 0), g.verts[2].pos[0]);
}

test "circle is closed and uses segment count" {
    var g = Gizmos{};
    g.clear();
    g.circle(.{ .x = 0, .y = 0, .z = 0 }, Vector3.up(), 1.0, 8);
    // 8 segments → 8 lines → 16 vertices.
    try std.testing.expectEqual(@as(usize, 16), g.vert_count);
}

test "label stores text and color" {
    var g = Gizmos{};
    g.clear();
    g.setColor(.green);
    g.label(.{ .x = 1, .y = 2, .z = 3 }, "node");
    try std.testing.expectEqual(@as(usize, 1), g.label_count);
    try std.testing.expectEqualStrings("node", g.labels[0].slice());
    try std.testing.expectEqual(@as(f32, 3), g.labels[0].pos[2]);
}

test "buffers never overflow capacity" {
    var g = Gizmos{};
    g.clear();
    var i: usize = 0;
    while (i < Gizmos.MAX_VERTICES) : (i += 1)
        g.line(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 1, .y = 1, .z = 1 });
    try std.testing.expect(g.vert_count <= Gizmos.MAX_VERTICES);
}

test "line width is recorded per vertex" {
    var g = Gizmos{};
    g.clear();
    g.setLineWidth(4);
    g.line(.{ .x = 0, .y = 0, .z = 0 }, .{ .x = 1, .y = 0, .z = 0 });
    try std.testing.expectEqual(@as(f32, 4), g.verts[0].thickness);
    try std.testing.expectEqual(@as(f32, 4), g.verts[1].thickness);
    // clear() resets the width back to 1.
    g.clear();
    try std.testing.expectEqual(@as(f32, 1), g.line_width);
}

test "ribbon tapers width per endpoint" {
    var g = Gizmos{};
    g.clear();
    const pts = [_]Vector3{
        .{ .x = 0, .y = 0, .z = 0 },
        .{ .x = 1, .y = 0, .z = 0 },
        .{ .x = 2, .y = 0, .z = 0 },
    };
    const w = [_]f32{ 6, 3, 0 };
    g.ribbon(&pts, &w);
    // 2 segments → 4 vertices, widths following the per-point taper.
    try std.testing.expectEqual(@as(usize, 4), g.vert_count);
    try std.testing.expectEqual(@as(f32, 6), g.verts[0].thickness);
    try std.testing.expectEqual(@as(f32, 3), g.verts[1].thickness);
    try std.testing.expectEqual(@as(f32, 3), g.verts[2].thickness);
    try std.testing.expectEqual(@as(f32, 0), g.verts[3].thickness);
}

test "wireSphere draws three great circles" {
    var g = Gizmos{};
    g.clear();
    g.wireSphere(.{ .x = 0, .y = 0, .z = 0 }, 2.0);
    // 3 circles × 32 segments × 2 vertices.
    try std.testing.expectEqual(@as(usize, 3 * Gizmos.CIRCLE_SEGMENTS * 2), g.vert_count);
}
