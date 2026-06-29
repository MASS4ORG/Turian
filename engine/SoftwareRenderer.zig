/// Standalone software 3D rasterizer for game builds.
/// Renders a scene into a fixed-size RGBA8 pixel buffer that the caller
/// can blit to a window via any 2D surface API.
const std = @import("std");
const engine = @import("root.zig");
const Matrix4 = engine.Matrix4;
const Vector3 = engine.Vector3;

/// Viewport width in pixels.
pub const VP_W: u32 = 640;
/// Viewport height in pixels.
pub const VP_H: u32 = 480;

const NEAR: f32 = 0.05;

var g_pixels: [VP_W * VP_H * 4]u8 = undefined;
var g_zbuf: [VP_W * VP_H]f32 = undefined;

const MAX_CACHED = 16;
const CachedMesh = struct {
    path_buf: [256]u8 = undefined,
    path_len: usize = 0,
    mesh: engine.Mesh = undefined,

    fn path(self: *const @This()) []const u8 {
        return self.path_buf[0..self.path_len];
    }
};
var g_cache: [MAX_CACHED]CachedMesh = undefined;
var g_cache_len: usize = 0;

/// Optional GUID→path resolver. Register via setGuidResolver() at startup.
/// Receives a GUID string and returns the corresponding asset file path,
/// or an empty slice when the GUID is not found.
var g_guid_resolver: ?*const fn (guid: []const u8) []const u8 = null;

/// Register a function that maps GUID strings to asset file paths.
/// Call this once at game startup before rendering.
pub fn setGuidResolver(f: *const fn (guid: []const u8) []const u8) void {
    g_guid_resolver = f;
}

/// Mesh bytes supplied by an asset source (e.g. an `.oap` package) instead of a
/// file path. `ext` (like ".obj") selects the parser.
pub const MeshSource = struct {
    bytes: []const u8,
    ext: []const u8,
    /// When true, the renderer frees `bytes` with the page allocator after
    /// parsing (the source allocated them just for this call).
    owned: bool = true,
};

/// Optional GUID→mesh-bytes source. When set, it takes precedence over the
/// path-based GUID resolver — this is how a packaged (`.oap`) game loads meshes
/// without touching the loose filesystem.
var g_mesh_source: ?*const fn (guid: []const u8) ?MeshSource = null;

/// Register a function that returns mesh bytes for a GUID (from a package or any
/// `AssetProvider`). Call once at game startup before rendering.
pub fn setMeshSource(f: *const fn (guid: []const u8) ?MeshSource) void {
    g_mesh_source = f;
}

/// Material (`.material`) bytes supplied by an asset source, keyed by GUID.
pub const MaterialSource = struct {
    bytes: []const u8,
    /// When true, the renderer frees `bytes` with the page allocator after parsing.
    owned: bool = true,
};

/// Encoded image bytes (PNG/JPG/...) supplied by an asset source, keyed by GUID.
/// stb_image sniffs the format from the bytes, so no extension is needed.
pub const TextureSource = struct {
    bytes: []const u8,
    /// When true, the renderer frees `bytes` with the page allocator after decoding.
    owned: bool = true,
};

/// Optional GUID→material-bytes source. Lets the renderer resolve a mesh
/// renderer's assigned material (base colour, emissive, texture maps).
var g_material_source: ?*const fn (guid: []const u8) ?MaterialSource = null;
/// Optional GUID→image-bytes source for material texture maps.
var g_texture_source: ?*const fn (guid: []const u8) ?TextureSource = null;

/// Register a function that returns `.material` bytes for a GUID. Call once at
/// game startup before rendering. Without it, meshes render white/unlit-flat.
pub fn setMaterialSource(f: *const fn (guid: []const u8) ?MaterialSource) void {
    g_material_source = f;
}

/// Register a function that returns image bytes for a texture GUID. Call once at
/// game startup before rendering. Without it, materials render untextured.
pub fn setTextureSource(f: *const fn (guid: []const u8) ?TextureSource) void {
    g_texture_source = f;
}

// ── Texture cache ───────────────────────────────────────────────────────────
// Decoded textures cached by GUID. Stable addresses (fixed array), so resolved
// materials can hold pointers to entries. Failed loads are cached as !ok to
// avoid retrying every frame.

const MAX_TEXTURES = 32;
const CachedTexture = struct {
    guid_buf: [40]u8 = undefined,
    guid_len: usize = 0,
    tex: engine.Texture = undefined,
    ok: bool = false,

    fn guid(self: *const @This()) []const u8 {
        return self.guid_buf[0..self.guid_len];
    }

    /// Nearest-neighbour sample with repeat wrap. Returns RGBA in 0..1.
    fn sample(self: *const @This(), u: f32, v: f32) [4]f32 {
        const w = self.tex.width;
        const h = self.tex.height;
        if (w == 0 or h == 0) return .{ 1, 1, 1, 1 };
        const uu = u - @floor(u);
        const vv = v - @floor(v);
        var xi: usize = @intFromFloat(uu * @as(f32, @floatFromInt(w)));
        var yi: usize = @intFromFloat(vv * @as(f32, @floatFromInt(h)));
        if (xi >= w) xi = w - 1;
        if (yi >= h) yi = h - 1;
        const idx = (yi * w + xi) * 4;
        const d = self.tex.data;
        return .{
            @as(f32, @floatFromInt(d[idx + 0])) / 255.0,
            @as(f32, @floatFromInt(d[idx + 1])) / 255.0,
            @as(f32, @floatFromInt(d[idx + 2])) / 255.0,
            @as(f32, @floatFromInt(d[idx + 3])) / 255.0,
        };
    }
};
var g_tex_cache: [MAX_TEXTURES]CachedTexture = undefined;
var g_tex_cache_len: usize = 0;

/// Resolve a texture GUID to a decoded, cached texture, or null when unset or
/// unresolvable.
fn resolveTexture(guid_str: []const u8) ?*CachedTexture {
    if (guid_str.len == 0) return null;
    for (g_tex_cache[0..g_tex_cache_len]) |*e| {
        if (std.mem.eql(u8, e.guid(), guid_str)) return if (e.ok) e else null;
    }
    if (g_texture_source == null or g_tex_cache_len >= MAX_TEXTURES) return null;

    const e = &g_tex_cache[g_tex_cache_len];
    g_tex_cache_len += 1;
    const l = @min(guid_str.len, e.guid_buf.len);
    @memcpy(e.guid_buf[0..l], guid_str[0..l]);
    e.guid_len = l;
    e.ok = false;

    const src = (g_texture_source.?)(guid_str) orelse return null;
    defer if (src.owned) std.heap.page_allocator.free(@constCast(src.bytes));
    e.tex = engine.assets.loadTextureFromMemory(std.heap.page_allocator, src.bytes) catch return null;
    e.ok = true;
    return e;
}

// ── Material cache ──────────────────────────────────────────────────────────

/// A material resolved for software shading. Texture fields point into the
/// (stable) texture cache.
const ResolvedMaterial = struct {
    base_color: [4]f32 = .{ 1, 1, 1, 1 },
    emissive: [3]f32 = .{ 0, 0, 0 },
    emissive_strength: f32 = 0,
    albedo: ?*CachedTexture = null,
    emissive_tex: ?*CachedTexture = null,
};

const MAX_MATERIALS = 32;
const CachedMaterial = struct {
    guid_buf: [40]u8 = undefined,
    guid_len: usize = 0,
    mat: ResolvedMaterial = .{},

    fn guid(self: *const @This()) []const u8 {
        return self.guid_buf[0..self.guid_len];
    }
};
var g_mat_cache: [MAX_MATERIALS]CachedMaterial = undefined;
var g_mat_cache_len: usize = 0;

/// Resolve a material GUID to its shading parameters (cached by GUID). Returns a
/// default white material when unset or unresolvable.
fn resolveMaterial(guid_str: []const u8) ResolvedMaterial {
    if (guid_str.len == 0) return .{};
    for (g_mat_cache[0..g_mat_cache_len]) |*e| {
        if (std.mem.eql(u8, e.guid(), guid_str)) return e.mat;
    }
    if (g_material_source == null or g_mat_cache_len >= MAX_MATERIALS) return .{};

    const src = (g_material_source.?)(guid_str) orelse return .{};
    defer if (src.owned) std.heap.page_allocator.free(@constCast(src.bytes));

    var mat = engine.Material.loadFromBytes(std.heap.page_allocator, src.bytes) catch return .{};
    defer mat.deinit(std.heap.page_allocator);

    const em = mat.vector("emissive", .{ 0, 0, 0, 1 });
    const resolved = ResolvedMaterial{
        .base_color = mat.vector("base_color", .{ 1, 1, 1, 1 }),
        .emissive = .{ em[0], em[1], em[2] },
        .emissive_strength = mat.scalar("emissive_strength", 0),
        .albedo = resolveTexture(mat.texture("albedo_map")),
        .emissive_tex = resolveTexture(mat.texture("emissive_map")),
    };

    const e = &g_mat_cache[g_mat_cache_len];
    g_mat_cache_len += 1;
    const l = @min(guid_str.len, e.guid_buf.len);
    @memcpy(e.guid_buf[0..l], guid_str[0..l]);
    e.guid_len = l;
    e.mat = resolved;
    return resolved;
}

/// Returns a read-only slice of the internal RGBA8 pixel buffer.
pub fn pixelsSlice() []const u8 {
    return &g_pixels;
}

/// Render all active mesh_renderers in `objects` into the internal pixel buffer.
pub fn renderScene(io: std.Io, objects: []const engine.SceneNode) void {
    var i: usize = 0;
    while (i < VP_W * VP_H) : (i += 1) {
        g_pixels[i * 4 + 0] = 36;
        g_pixels[i * 4 + 1] = 36;
        g_pixels[i * 4 + 2] = 40;
        g_pixels[i * 4 + 3] = 255;
    }
    @memset(&g_zbuf, std.math.floatMax(f32));

    var cam_pos = Vector3{ .x = 0, .y = 2, .z = -5 };
    var cam_rot = Vector3{};
    var cam_fov: f32 = 60.0;
    outer: for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* == .camera) {
                cam_pos = obj.transform.position;
                cam_rot = obj.transform.rotation;
                cam_fov = comp.camera.fov;
                break :outer;
            }
        }
    }

    const axes = buildViewAxes(cam_rot);
    const f_val = 1.0 / @tan(cam_fov * std.math.pi / 360.0);
    const aspect = @as(f32, VP_W) / @as(f32, VP_H);

    var lights: [8]LightInfo = undefined;
    var light_count: usize = 0;
    for (objects) |*obj| {
        if (!obj.active or light_count >= 8) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* == .light) {
                const lc = &comp.light;
                const rm = Matrix4.rotationEuler(obj.transform.rotation.x, obj.transform.rotation.y, obj.transform.rotation.z);
                const d = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
                const p = obj.transform.position;
                lights[light_count] = .{
                    .kind = lc.kind,
                    .pos = .{ p.x, p.y, p.z },
                    .dir = .{ d.x, d.y, d.z },
                    .color = .{ lc.color_r, lc.color_g, lc.color_b },
                    .intensity = lc.intensity,
                    .range = lc.range,
                    .cos_outer = @cos(lc.spot_angle * std.math.pi / 180.0),
                    .cos_inner = @cos(lc.spot_angle * (1.0 - lc.spot_softness) * std.math.pi / 180.0),
                };
                light_count += 1;
            }
        }
    }

    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* == .mesh_renderer) {
                const guid_str = comp.mesh_renderer.mesh.slice();
                if (guid_str.len == 0) continue;
                // Prefer a packaged asset source; fall back to loose-file paths.
                const mesh_opt = if (g_mesh_source != null)
                    getMeshFromSource(guid_str)
                else path_blk: {
                    const mp = if (g_guid_resolver) |resolve| resolve(guid_str) else guid_str;
                    if (mp.len == 0) break :path_blk null;
                    break :path_blk getMesh(io, mp);
                };
                if (mesh_opt) |mesh| {
                    const mat = resolveMaterial(comp.mesh_renderer.material.slice());
                    renderMesh(mesh, obj, cam_pos, axes, f_val, aspect, lights[0..light_count], mat);
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------

const LightInfo = struct {
    kind: engine.LightComponent.Kind,
    pos: [3]f32,
    dir: [3]f32,
    color: [3]f32,
    intensity: f32,
    range: f32,
    cos_outer: f32,
    cos_inner: f32,
};

const ViewAxes = struct { right: [3]f32, up: [3]f32, fwd: [3]f32 };

fn buildViewAxes(rot: Vector3) ViewAxes {
    const rm = Matrix4.rotationEuler(rot.x, rot.y, rot.z);
    const fv = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
    const fwd = [3]f32{ fv.x, fv.y, fv.z };
    var right = cross3(.{ 0, 1, 0 }, fwd);
    const rl = vlen(right);
    if (rl > 1e-6) right = .{ right[0] / rl, right[1] / rl, right[2] / rl } else right = .{ 1, 0, 0 };
    return .{ .right = right, .up = cross3(fwd, right), .fwd = fwd };
}

fn worldToView(wp: [3]f32, eye: Vector3, ax: ViewAxes) [3]f32 {
    const dx = wp[0] - eye.x;
    const dy = wp[1] - eye.y;
    const dz = wp[2] - eye.z;
    return .{
        ax.right[0] * dx + ax.right[1] * dy + ax.right[2] * dz,
        ax.up[0] * dx + ax.up[1] * dy + ax.up[2] * dz,
        ax.fwd[0] * dx + ax.fwd[1] * dy + ax.fwd[2] * dz,
    };
}

fn project(vp: [3]f32, f: f32, aspect: f32) ?[3]f32 {
    if (vp[2] < NEAR) return null;
    return .{
        (1.0 + (f / aspect) * (vp[0] / vp[2])) * 0.5 * @as(f32, VP_W),
        (1.0 - f * (vp[1] / vp[2])) * 0.5 * @as(f32, VP_H),
        vp[2],
    };
}

/// Per-vertex attributes interpolated across a triangle during rasterization.
const Varying = struct {
    n: [3]f32, // world-space normal (un-normalized; normalized per pixel)
    uv: [2]f32, // texture coordinates
    wp: [3]f32, // world-space position (for point/spot lighting)
};

fn renderMesh(
    mesh: *const engine.Mesh,
    obj: *const engine.SceneNode,
    cam_pos: Vector3,
    axes: ViewAxes,
    f: f32,
    aspect: f32,
    lights: []const LightInfo,
    mat: ResolvedMaterial,
) void {
    const t = &obj.transform;
    const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
        .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
        .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
    const rot_only = Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z);

    var ii: usize = 0;
    while (ii + 2 < mesh.indices.len) : (ii += 3) {
        const ia = mesh.indices[ii];
        const ib = mesh.indices[ii + 1];
        const ic = mesh.indices[ii + 2];
        if (ia >= mesh.vertices.len or ib >= mesh.vertices.len or ic >= mesh.vertices.len) continue;
        const va = &mesh.vertices[ia];
        const vb = &mesh.vertices[ib];
        const vc = &mesh.vertices[ic];

        const wa = mdl.transformPoint(.{ .x = va.px, .y = va.py, .z = va.pz });
        const wb = mdl.transformPoint(.{ .x = vb.px, .y = vb.py, .z = vb.pz });
        const wc = mdl.transformPoint(.{ .x = vc.px, .y = vc.py, .z = vc.pz });

        const na = rot_only.transformDirection(.{ .x = va.nx, .y = va.ny, .z = va.nz });
        const nb = rot_only.transformDirection(.{ .x = vb.nx, .y = vb.ny, .z = vb.nz });
        const nc = rot_only.transformDirection(.{ .x = vc.nx, .y = vc.ny, .z = vc.nz });

        // Backface cull using the geometric face normal.
        var wn = [3]f32{ (na.x + nb.x + nc.x) / 3.0, (na.y + nb.y + nc.y) / 3.0, (na.z + nb.z + nc.z) / 3.0 };
        const nl = vlen(wn);
        if (nl > 1e-6) wn = .{ wn[0] / nl, wn[1] / nl, wn[2] / nl };
        const cx = (wa.x + wb.x + wc.x) / 3.0;
        const cy = (wa.y + wb.y + wc.y) / 3.0;
        const cz = (wa.z + wb.z + wc.z) / 3.0;
        if (dot3(wn, .{ cam_pos.x - cx, cam_pos.y - cy, cam_pos.z - cz }) < 0) continue;

        const sa = project(worldToView(.{ wa.x, wa.y, wa.z }, cam_pos, axes), f, aspect) orelse continue;
        const sb = project(worldToView(.{ wb.x, wb.y, wb.z }, cam_pos, axes), f, aspect) orelse continue;
        const sc = project(worldToView(.{ wc.x, wc.y, wc.z }, cam_pos, axes), f, aspect) orelse continue;

        rasterizeTriangle(
            sa,
            sb,
            sc,
            .{ .n = .{ na.x, na.y, na.z }, .uv = .{ va.u, va.v }, .wp = .{ wa.x, wa.y, wa.z } },
            .{ .n = .{ nb.x, nb.y, nb.z }, .uv = .{ vb.u, vb.v }, .wp = .{ wb.x, wb.y, wb.z } },
            .{ .n = .{ nc.x, nc.y, nc.z }, .uv = .{ vc.u, vc.v }, .wp = .{ wc.x, wc.y, wc.z } },
            mat,
            lights,
        );
    }
}

/// Shade one fragment: albedo (base_color × albedo map) under ambient +
/// directional/point/spot lighting, plus emissive. Metallic/roughness and
/// normal/occlusion maps are GPU-viewport-only for now; this is the lightweight
/// game fallback (no shadows — "within software-renderer limits").
fn shadePixel(normal: [3]f32, world_pos: [3]f32, uv: [2]f32, mat: ResolvedMaterial, lights: []const LightInfo) [4]u8 {
    var n = normal;
    const len = vlen(n);
    if (len > 1e-6) n = .{ n[0] / len, n[1] / len, n[2] / len };

    var albedo = [3]f32{ mat.base_color[0], mat.base_color[1], mat.base_color[2] };
    if (mat.albedo) |tex| {
        const s = tex.sample(uv[0], uv[1]);
        albedo = .{ albedo[0] * s[0], albedo[1] * s[1], albedo[2] * s[2] };
    }

    var lit = [3]f32{ 0.08, 0.08, 0.08 };
    for (lights) |l| {
        var l_dir: [3]f32 = undefined;
        var atten: f32 = 1.0;
        if (l.kind == .directional) {
            l_dir = .{ -l.dir[0], -l.dir[1], -l.dir[2] };
        } else {
            var to_light = [3]f32{ l.pos[0] - world_pos[0], l.pos[1] - world_pos[1], l.pos[2] - world_pos[2] };
            const dist = vlen(to_light);
            if (dist > 1e-4) to_light = .{ to_light[0] / dist, to_light[1] / dist, to_light[2] / dist };
            l_dir = to_light;
            const range = @max(l.range, 1e-4);
            const d2 = dist * dist;
            atten = @max(0.0, 1.0 - (d2 / (range * range)));
            atten = atten * atten / (1.0 + d2);
            if (l.kind == .spot) {
                const cos_a = dot3(l.dir, .{ -l_dir[0], -l_dir[1], -l_dir[2] });
                const cone = std.math.clamp((cos_a - l.cos_outer) / @max(l.cos_inner - l.cos_outer, 1e-4), 0.0, 1.0);
                atten *= cone;
            }
        }
        if (atten <= 0.0) continue;
        const ndl = @max(0.0, dot3(n, l_dir)) * l.intensity * atten;
        lit[0] += ndl * l.color[0];
        lit[1] += ndl * l.color[1];
        lit[2] += ndl * l.color[2];
    }

    var col = [3]f32{ albedo[0] * lit[0], albedo[1] * lit[1], albedo[2] * lit[2] };

    var emis = [3]f32{
        mat.emissive[0] * mat.emissive_strength,
        mat.emissive[1] * mat.emissive_strength,
        mat.emissive[2] * mat.emissive_strength,
    };
    if (mat.emissive_tex) |tex| {
        const s = tex.sample(uv[0], uv[1]);
        emis = .{ emis[0] * s[0], emis[1] * s[1], emis[2] * s[2] };
    }
    col = .{ col[0] + emis[0], col[1] + emis[1], col[2] + emis[2] };

    return .{
        @intFromFloat(@min(255.0, @max(0.0, col[0]) * 255.0)),
        @intFromFloat(@min(255.0, @max(0.0, col[1]) * 255.0)),
        @intFromFloat(@min(255.0, @max(0.0, col[2]) * 255.0)),
        255,
    };
}

fn rasterizeTriangle(
    a: [3]f32,
    b: [3]f32,
    c: [3]f32,
    va: Varying,
    vb: Varying,
    vc: Varying,
    mat: ResolvedMaterial,
    lights: []const LightInfo,
) void {
    const x0 = @max(0, @as(i32, @intFromFloat(@floor(@min(@min(a[0], b[0]), c[0])))));
    const x1 = @min(@as(i32, VP_W) - 1, @as(i32, @intFromFloat(@ceil(@max(@max(a[0], b[0]), c[0])))));
    const y0 = @max(0, @as(i32, @intFromFloat(@floor(@min(@min(a[1], b[1]), c[1])))));
    const y1 = @min(@as(i32, VP_H) - 1, @as(i32, @intFromFloat(@ceil(@max(@max(a[1], b[1]), c[1])))));
    if (x0 > x1 or y0 > y1) return;

    const area = edge2d(a[0], a[1], b[0], b[1], c[0], c[1]);
    if (@abs(area) < 1.0) return;
    const inv_za = 1.0 / a[2];
    const inv_zb = 1.0 / b[2];
    const inv_zc = 1.0 / c[2];

    var y = y0;
    while (y <= y1) : (y += 1) {
        var x = x0;
        while (x <= x1) : (x += 1) {
            const px = @as(f32, @floatFromInt(x)) + 0.5;
            const py = @as(f32, @floatFromInt(y)) + 0.5;
            const w0 = edge2d(b[0], b[1], c[0], c[1], px, py);
            const w1 = edge2d(c[0], c[1], a[0], a[1], px, py);
            const w2 = edge2d(a[0], a[1], b[0], b[1], px, py);
            if (area > 0) {
                if (w0 < 0 or w1 < 0 or w2 < 0) continue;
            } else {
                if (w0 > 0 or w1 > 0 or w2 > 0) continue;
            }

            const l0 = w0 / area;
            const l1 = w1 / area;
            const l2 = w2 / area;
            const inv_z = l0 * inv_za + l1 * inv_zb + l2 * inv_zc;
            if (inv_z <= 0) continue;
            const z = 1.0 / inv_z;

            const idx = @as(usize, @intCast(y)) * VP_W + @as(usize, @intCast(x));
            if (z >= g_zbuf[idx]) continue;
            g_zbuf[idx] = z;

            const pa = l0 * inv_za;
            const pb = l1 * inv_zb;
            const pc = l2 * inv_zc;
            const nrm = [3]f32{
                (va.n[0] * pa + vb.n[0] * pb + vc.n[0] * pc) * z,
                (va.n[1] * pa + vb.n[1] * pb + vc.n[1] * pc) * z,
                (va.n[2] * pa + vb.n[2] * pb + vc.n[2] * pc) * z,
            };
            const uv = [2]f32{
                (va.uv[0] * pa + vb.uv[0] * pb + vc.uv[0] * pc) * z,
                (va.uv[1] * pa + vb.uv[1] * pb + vc.uv[1] * pc) * z,
            };
            const wp = [3]f32{
                (va.wp[0] * pa + vb.wp[0] * pb + vc.wp[0] * pc) * z,
                (va.wp[1] * pa + vb.wp[1] * pb + vc.wp[1] * pc) * z,
                (va.wp[2] * pa + vb.wp[2] * pb + vc.wp[2] * pc) * z,
            };

            const color = shadePixel(nrm, wp, uv, mat, lights);
            g_pixels[idx * 4 + 0] = color[0];
            g_pixels[idx * 4 + 1] = color[1];
            g_pixels[idx * 4 + 2] = color[2];
            g_pixels[idx * 4 + 3] = color[3];
        }
    }
}

fn edge2d(ax: f32, ay: f32, bx: f32, by: f32, px: f32, py: f32) f32 {
    return (bx - ax) * (py - ay) - (by - ay) * (px - ax);
}

/// Resolve a mesh by GUID through the registered asset source, caching by GUID.
fn getMeshFromSource(guid: []const u8) ?*const engine.Mesh {
    for (g_cache[0..g_cache_len]) |*e| {
        if (std.mem.eql(u8, e.path(), guid)) return &e.mesh;
    }
    if (g_cache_len >= MAX_CACHED) return null;
    const src = (g_mesh_source.?)(guid) orelse return null;
    defer if (src.owned) std.heap.page_allocator.free(@constCast(src.bytes));
    const mesh = engine.assets.loadMeshFromMemory(std.heap.page_allocator, src.bytes, src.ext) catch return null;
    const e = &g_cache[g_cache_len];
    const klen = @min(guid.len, e.path_buf.len);
    @memcpy(e.path_buf[0..klen], guid[0..klen]);
    e.path_len = klen;
    e.mesh = mesh;
    g_cache_len += 1;
    return &e.mesh;
}

fn getMesh(io: std.Io, path: []const u8) ?*const engine.Mesh {
    for (g_cache[0..g_cache_len]) |*e| {
        if (std.mem.eql(u8, e.path(), path)) return &e.mesh;
    }
    if (g_cache_len >= MAX_CACHED) return null;
    const mesh = engine.assets.loadMesh(std.heap.page_allocator, io, path) catch return null;
    const e = &g_cache[g_cache_len];
    const plen = @min(path.len, e.path_buf.len);
    @memcpy(e.path_buf[0..plen], path[0..plen]);
    e.path_len = plen;
    e.mesh = mesh;
    g_cache_len += 1;
    return &e.mesh;
}

fn cross3(a: [3]f32, b: [3]f32) [3]f32 {
    return .{ a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
}
fn dot3(a: [3]f32, b: [3]f32) f32 {
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}
fn vlen(v: [3]f32) f32 {
    return @sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
}

// ── Tests ─────────────────────────────────────────────────────────────────────

test "resolveMaterial reads base_color and emissive from a material source" {
    g_mat_cache_len = 0;
    g_tex_cache_len = 0;
    g_material_source = null;

    // Serialize a material: red base_color, emissive strength 2.
    var sbuf: [1024]u8 = undefined;
    var w = std.Io.Writer.fixed(&sbuf);
    var vectors = [_]engine.Material.VectorParam{
        .{ .name = "base_color", .value = .{ 1, 0, 0, 1 } },
        .{ .name = "emissive", .value = .{ 0, 1, 0, 1 } },
    };
    var scalars = [_]engine.Material.ScalarParam{
        .{ .name = "emissive_strength", .value = 2.0 },
    };
    const mat = engine.Material{ .vectors = &vectors, .scalars = &scalars };
    try mat.serialize(&w);

    const Src = struct {
        var bytes: [1024]u8 = undefined;
        var len: usize = 0;
        fn get(guid: []const u8) ?MaterialSource {
            if (!std.mem.eql(u8, guid, "mat-red")) return null;
            return .{ .bytes = bytes[0..len], .owned = false };
        }
    };
    @memcpy(Src.bytes[0..w.buffered().len], w.buffered());
    Src.len = w.buffered().len;
    setMaterialSource(&Src.get);

    const r = resolveMaterial("mat-red");
    try std.testing.expectEqual([4]f32{ 1, 0, 0, 1 }, r.base_color);
    try std.testing.expectEqual([3]f32{ 0, 1, 0 }, r.emissive);
    try std.testing.expectEqual(@as(f32, 2.0), r.emissive_strength);
    try std.testing.expect(r.albedo == null);

    // Cached on the second call (same values).
    const r2 = resolveMaterial("mat-red");
    try std.testing.expectEqual([4]f32{ 1, 0, 0, 1 }, r2.base_color);

    // Empty / unknown GUIDs fall back to the default white material.
    try std.testing.expectEqual([4]f32{ 1, 1, 1, 1 }, resolveMaterial("").base_color);
    try std.testing.expectEqual([4]f32{ 1, 1, 1, 1 }, resolveMaterial("unknown").base_color);

    g_material_source = null;
    g_mat_cache_len = 0;
    g_tex_cache_len = 0;
}

test "shadePixel lights a surface by light type" {
    const white = ResolvedMaterial{ .base_color = .{ 1, 1, 1, 1 } };
    const up = [3]f32{ 0, 1, 0 };
    const origin = [3]f32{ 0, 0, 0 };
    const uv = [2]f32{ 0, 0 };

    // Directional light pointing straight down lights an upward-facing surface.
    const dir = [_]LightInfo{.{
        .kind = .directional,
        .pos = .{ 0, 0, 0 },
        .dir = .{ 0, -1, 0 },
        .color = .{ 1, 1, 1 },
        .intensity = 1,
        .range = 10,
        .cos_outer = -1,
        .cos_inner = 1,
    }};
    const lit = shadePixel(up, origin, uv, white, &dir);
    try std.testing.expect(lit[0] > 100); // clearly lit, not just ambient

    // A point light close by is brighter than the same light far away.
    var pt = [_]LightInfo{.{
        .kind = .point,
        .pos = .{ 0, 1, 0 },
        .dir = .{ 0, 0, 1 },
        .color = .{ 1, 1, 1 },
        .intensity = 4,
        .range = 20,
        .cos_outer = -1,
        .cos_inner = 1,
    }};
    const near = shadePixel(up, origin, uv, white, &pt);
    pt[0].pos = .{ 0, 8, 0 };
    const far = shadePixel(up, origin, uv, white, &pt);
    try std.testing.expect(near[0] > far[0]);

    // A spot light aimed away from the fragment contributes nothing beyond ambient.
    const spot_off = [_]LightInfo{.{
        .kind = .spot,
        .pos = .{ 0, 1, 0 },
        .dir = .{ 1, 0, 0 }, // pointing +X, fragment is straight below
        .color = .{ 1, 1, 1 },
        .intensity = 8,
        .range = 20,
        .cos_outer = @cos(20.0 * std.math.pi / 180.0),
        .cos_inner = @cos(15.0 * std.math.pi / 180.0),
    }};
    const dark = shadePixel(up, origin, uv, white, &spot_off);
    try std.testing.expectEqual(dark[0], shadePixel(up, origin, uv, white, &[_]LightInfo{})[0]);
}
