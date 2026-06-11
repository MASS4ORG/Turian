//! Material asset — a collection of shader parameter values and resource
//! bindings, serialized to a `.material` file as ZON (same on-disk family as
//! scenes).
//!
//! A material references a shader by GUID and stores values for the parameters
//! that shader exposes. Colours and vectors share one `[4]f32` storage form;
//! the shader's `ParamKind` decides how each is interpreted and edited. Shader
//! and texture references are stored as stable asset GUIDs so they survive
//! renames and moves.
//!
//! A material deliberately does NOT contain mesh data, compiled shader
//! binaries, or renderer pipeline objects — only authoring data.
//!
//! Ownership: a `Material` produced by `load`/`loadFromBytes` owns its slices
//! via the parse allocator; release them with `deinit`. Values assembled by a
//! caller (e.g. the editor, pointing slices at its own buffers) must NOT be
//! passed to `deinit`; just serialize them with `save`/`serialize`.
const std = @import("std");
const shader = @import("Shader.zig");

/// A material asset.
pub const Material = struct {
    /// Current material format version. Bump when the layout changes and add a
    /// migration in `migrate` so older assets keep loading.
    pub const CURRENT_VERSION: u32 = 1;

    /// Maximum parameters of one kind handled by the stack-based default builder.
    pub const MAX_PARAMS = 64;

    /// How fragments blend against the framebuffer. `disabled` is fully opaque.
    pub const BlendMode = enum { disabled, alpha, additive };

    /// Which triangle faces are culled.
    pub const CullMode = enum { back, front, none };

    /// Optional fixed-function render state a material may override.
    pub const RenderState = struct {
        blend: BlendMode = .disabled,
        cull: CullMode = .back,
        depth_write: bool = true,
        depth_test: bool = true,
    };

    /// A named scalar parameter value.
    pub const ScalarParam = struct {
        name: []const u8,
        value: f32 = 0,
    };

    /// A named vector/colour parameter value (RGBA / XYZW).
    pub const VectorParam = struct {
        name: []const u8,
        value: [4]f32 = .{ 0, 0, 0, 1 },
    };

    /// A named texture binding, referencing a texture asset by GUID.
    pub const TextureParam = struct {
        name: []const u8,
        /// Texture asset GUID string; empty means unbound.
        texture: []const u8 = "",
    };

    /// Format version for migration.
    version: u32 = CURRENT_VERSION,
    /// Shader asset GUID. Empty resolves to the built-in PBR shader.
    shader: []const u8 = shader.pbr_guid,
    /// Scalar parameter values.
    scalars: []ScalarParam = &.{},
    /// Vector and colour parameter values (both stored as `[4]f32`).
    vectors: []VectorParam = &.{},
    /// Texture bindings.
    textures: []TextureParam = &.{},
    /// Optional render-state overrides.
    render: RenderState = .{},

    // ── Accessors ──────────────────────────────────────────────────────────

    /// Returns the value of scalar `name`, or `fallback` if unset.
    pub fn scalar(self: Material, name: []const u8, fallback: f32) f32 {
        for (self.scalars) |p| {
            if (std.mem.eql(u8, p.name, name)) return p.value;
        }
        return fallback;
    }

    /// Returns the value of vector/colour `name`, or `fallback` if unset.
    pub fn vector(self: Material, name: []const u8, fallback: [4]f32) [4]f32 {
        for (self.vectors) |p| {
            if (std.mem.eql(u8, p.name, name)) return p.value;
        }
        return fallback;
    }

    /// Returns the texture GUID bound to `name`, or empty if unbound.
    pub fn texture(self: Material, name: []const u8) []const u8 {
        for (self.textures) |p| {
            if (std.mem.eql(u8, p.name, name)) return p.texture;
        }
        return "";
    }

    /// Resolve the shader this material uses (built-in lookup, PBR fallback).
    pub fn shaderDef(self: Material) shader.ShaderDef {
        return shader.resolve(self.shader);
    }

    // ── Load ───────────────────────────────────────────────────────────────

    /// Parse a material from in-memory `.material` (ZON) bytes. The returned
    /// value owns its slices; free with `deinit`. `bytes` need not be
    /// NUL-terminated.
    pub fn loadFromBytes(allocator: std.mem.Allocator, bytes: []const u8) !Material {
        const z = try allocator.dupeZ(u8, bytes);
        defer allocator.free(z);
        var mat = try std.zon.parse.fromSliceAlloc(Material, allocator, z, null, .{});
        migrate(&mat);
        return mat;
    }

    /// Load a material from a `.material` file. The returned value owns its
    /// slices; free with `deinit`.
    pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Material {
        var file = try std.Io.Dir.cwd().openFile(io, path, .{});
        defer file.close(io);
        var fbuf: [4096]u8 = undefined;
        var reader = file.reader(io, &fbuf);
        const content = try reader.interface.allocRemaining(allocator, .unlimited);
        defer allocator.free(content);
        return loadFromBytes(allocator, content);
    }

    /// Free slices owned by a material produced via `load`/`loadFromBytes`.
    pub fn deinit(self: Material, allocator: std.mem.Allocator) void {
        std.zon.parse.free(allocator, self);
    }

    // ── Save ───────────────────────────────────────────────────────────────

    /// Serialize this material as ZON into `writer`.
    pub fn serialize(self: Material, writer: *std.Io.Writer) !void {
        try std.zon.stringify.serialize(self, .{}, writer);
    }

    /// Write this material to `path` as a `.material` ZON file.
    pub fn save(self: Material, io: std.Io, path: []const u8) !void {
        var buf: [1024 * 64]u8 = undefined;
        var writer = std.Io.Writer.fixed(&buf);
        try self.serialize(&writer);
        try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = writer.buffered() });
    }

    // ── Defaults ───────────────────────────────────────────────────────────

    /// Serialize a fresh material for `sh` (every parameter at its default) as
    /// ZON into `writer`, without allocating. Used to author a new material file.
    pub fn serializeDefault(sh: shader.ShaderDef, writer: *std.Io.Writer) !void {
        var scalars: [MAX_PARAMS]ScalarParam = undefined;
        var vectors: [MAX_PARAMS]VectorParam = undefined;
        var textures: [MAX_PARAMS]TextureParam = undefined;
        var ns: usize = 0;
        var nv: usize = 0;
        var nt: usize = 0;

        for (sh.params) |p| {
            switch (p.kind) {
                .scalar => {
                    if (ns >= MAX_PARAMS) continue;
                    scalars[ns] = .{ .name = p.name, .value = p.default_scalar };
                    ns += 1;
                },
                .texture => {
                    if (nt >= MAX_PARAMS) continue;
                    textures[nt] = .{ .name = p.name, .texture = "" };
                    nt += 1;
                },
                .vec2, .vec3, .vec4, .color => {
                    if (nv >= MAX_PARAMS) continue;
                    vectors[nv] = .{ .name = p.name, .value = p.default_vec };
                    nv += 1;
                },
            }
        }

        const mat = Material{
            .shader = sh.guid,
            .scalars = scalars[0..ns],
            .vectors = vectors[0..nv],
            .textures = textures[0..nt],
        };
        try mat.serialize(writer);
    }

    /// Write a fresh default material for `sh` to `path`.
    pub fn saveDefault(sh: shader.ShaderDef, io: std.Io, path: []const u8) !void {
        var buf: [1024 * 64]u8 = undefined;
        var writer = std.Io.Writer.fixed(&buf);
        try serializeDefault(sh, &writer);
        try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = writer.buffered() });
    }

    // ── Presets ────────────────────────────────────────────────────────────────

    /// Named material preset: override values applied on top of a shader's defaults.
    /// Params not listed keep their shader-default values.
    pub const Preset = struct {
        name: []const u8,
        /// Stable built-in GUID (non-empty for built-ins; empty for user presets).
        /// Same UUID namespace as pbr_guid: 00000000-0000-4000-8000-0000000001xx.
        guid: []const u8 = "",
        scalars: []const ScalarParam = &.{},
        vectors: []const VectorParam = &.{},
        render: RenderState = .{},
    };

    /// Built-in presets shown in the asset browser "Create Material" menu and
    /// in the material picker for asset reference fields.
    pub const presets: []const Preset = &.{
        .{ .name = "Default", .guid = "00000000-0000-4000-8000-000000000100" },
        .{
            .name = "Metal",
            .guid = "00000000-0000-4000-8000-000000000101",
            .scalars = &.{
                .{ .name = "metallic", .value = 1.0 },
                .{ .name = "roughness", .value = 0.15 },
            },
            .vectors = &.{
                .{ .name = "base_color", .value = .{ 0.9, 0.9, 0.9, 1.0 } },
            },
        },
        .{
            .name = "Plastic",
            .guid = "00000000-0000-4000-8000-000000000102",
            .scalars = &.{
                .{ .name = "metallic", .value = 0.0 },
                .{ .name = "roughness", .value = 0.35 },
            },
        },
        .{
            .name = "Emissive",
            .guid = "00000000-0000-4000-8000-000000000103",
            .scalars = &.{
                .{ .name = "emissive_strength", .value = 3.0 },
            },
            .vectors = &.{
                .{ .name = "base_color", .value = .{ 0.0, 0.0, 0.0, 1.0 } },
                .{ .name = "emissive", .value = .{ 1.0, 0.9, 0.7, 1.0 } },
            },
        },
        .{
            .name = "Glass",
            .guid = "00000000-0000-4000-8000-000000000104",
            .scalars = &.{
                .{ .name = "roughness", .value = 0.05 },
            },
            .vectors = &.{
                .{ .name = "base_color", .value = .{ 0.9, 0.95, 1.0, 0.15 } },
            },
            .render = .{ .blend = .alpha, .cull = .none, .depth_write = false, .depth_test = true },
        },
    };

    /// Serialize a built-in preset identified by `guid` into `buf`.
    /// Returns the written slice, or null if `guid` matches no built-in.
    pub fn builtinBytes(guid: []const u8, buf: []u8) ?[]const u8 {
        const p = for (presets) |p| {
            if (p.guid.len > 0 and std.mem.eql(u8, p.guid, guid)) break p;
        } else return null;
        var writer = std.Io.Writer.fixed(buf);
        serializePreset(p, shader.pbr, &writer) catch return null;
        return writer.buffered();
    }

    /// Serialize a material from `preset`, applying its overrides on top of `sh`'s defaults.
    pub fn serializePreset(preset: Preset, sh: shader.ShaderDef, writer: *std.Io.Writer) !void {
        var scalars: [MAX_PARAMS]ScalarParam = undefined;
        var vectors: [MAX_PARAMS]VectorParam = undefined;
        var textures: [MAX_PARAMS]TextureParam = undefined;
        var ns: usize = 0;
        var nv: usize = 0;
        var nt: usize = 0;

        for (sh.params) |p| {
            switch (p.kind) {
                .scalar => {
                    if (ns >= MAX_PARAMS) continue;
                    var val = p.default_scalar;
                    for (preset.scalars) |ov| {
                        if (std.mem.eql(u8, ov.name, p.name)) {
                            val = ov.value;
                            break;
                        }
                    }
                    scalars[ns] = .{ .name = p.name, .value = val };
                    ns += 1;
                },
                .texture => {
                    if (nt >= MAX_PARAMS) continue;
                    textures[nt] = .{ .name = p.name, .texture = "" };
                    nt += 1;
                },
                .vec2, .vec3, .vec4, .color => {
                    if (nv >= MAX_PARAMS) continue;
                    var val = p.default_vec;
                    for (preset.vectors) |ov| {
                        if (std.mem.eql(u8, ov.name, p.name)) {
                            val = ov.value;
                            break;
                        }
                    }
                    vectors[nv] = .{ .name = p.name, .value = val };
                    nv += 1;
                },
            }
        }

        const mat = Material{
            .shader = sh.guid,
            .scalars = scalars[0..ns],
            .vectors = vectors[0..nv],
            .textures = textures[0..nt],
            .render = preset.render,
        };
        try mat.serialize(writer);
    }

    /// Write a material from `preset` to `path`.
    pub fn savePreset(preset: Preset, sh: shader.ShaderDef, io: std.Io, path: []const u8) !void {
        var buf: [1024 * 64]u8 = undefined;
        var writer = std.Io.Writer.fixed(&buf);
        try serializePreset(preset, sh, &writer);
        try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = writer.buffered() });
    }
};

/// Upgrade a just-parsed material in place to `CURRENT_VERSION`. New versions
/// add cases here so old assets keep loading. Currently a no-op stamp.
fn migrate(mat: *Material) void {
    // v0 / unset → v1: nothing structural changed; just stamp the version.
    if (mat.version < Material.CURRENT_VERSION) mat.version = Material.CURRENT_VERSION;
}

// ── Tests ─────────────────────────────────────────────────────────────────────

test "default material round-trips through ZON" {
    const a = std.testing.allocator;

    var buf: [1024 * 8]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buf);
    try Material.serializeDefault(shader.pbr, &writer);

    var mat = try Material.loadFromBytes(a, writer.buffered());
    defer mat.deinit(a);

    try std.testing.expectEqual(Material.CURRENT_VERSION, mat.version);
    try std.testing.expectEqualStrings(shader.pbr_guid, mat.shader);
    try std.testing.expectEqual(@as(f32, 0.5), mat.scalar("roughness", 0));
    try std.testing.expectEqual(@as(f32, 0.0), mat.scalar("metallic", 1));
    try std.testing.expectEqual([4]f32{ 1, 1, 1, 1 }, mat.vector("base_color", .{ 0, 0, 0, 0 }));
    try std.testing.expectEqualStrings("", mat.texture("albedo_map"));
}

test "edited material round-trips values and references" {
    const a = std.testing.allocator;

    var scalars = [_]Material.ScalarParam{
        .{ .name = "metallic", .value = 0.8 },
        .{ .name = "roughness", .value = 0.2 },
    };
    var vectors = [_]Material.VectorParam{
        .{ .name = "base_color", .value = .{ 0.1, 0.2, 0.3, 1.0 } },
    };
    var textures = [_]Material.TextureParam{
        .{ .name = "albedo_map", .texture = "11111111-1111-4111-8111-111111111111" },
    };
    const src = Material{
        .shader = shader.pbr_guid,
        .scalars = &scalars,
        .vectors = &vectors,
        .textures = &textures,
        .render = .{ .blend = .alpha, .cull = .none, .depth_write = false },
    };

    var sbuf: [1024 * 8]u8 = undefined;
    var writer = std.Io.Writer.fixed(&sbuf);
    try src.serialize(&writer);

    var mat = try Material.loadFromBytes(a, writer.buffered());
    defer mat.deinit(a);

    try std.testing.expectEqual(@as(f32, 0.8), mat.scalar("metallic", 0));
    try std.testing.expectEqual(@as(f32, 0.2), mat.scalar("roughness", 0));
    try std.testing.expectEqual([4]f32{ 0.1, 0.2, 0.3, 1.0 }, mat.vector("base_color", .{ 0, 0, 0, 0 }));
    try std.testing.expectEqualStrings("11111111-1111-4111-8111-111111111111", mat.texture("albedo_map"));
    try std.testing.expectEqual(Material.BlendMode.alpha, mat.render.blend);
    try std.testing.expectEqual(Material.CullMode.none, mat.render.cull);
    try std.testing.expectEqual(false, mat.render.depth_write);
}

test "missing parameters fall back to provided defaults" {
    const a = std.testing.allocator;
    const empty = ".{}"; // empty material literal — all fields defaulted
    var mat = try Material.loadFromBytes(a, empty);
    defer mat.deinit(a);
    try std.testing.expectEqual(@as(f32, 1.5), mat.scalar("absent", 1.5));
    try std.testing.expectEqual([4]f32{ 9, 9, 9, 9 }, mat.vector("absent", .{ 9, 9, 9, 9 }));
    try std.testing.expectEqualStrings("", mat.texture("absent"));
}

test "preset Metal overrides metallic and roughness" {
    const a = std.testing.allocator;
    const metal = for (Material.presets) |p| {
        if (std.mem.eql(u8, p.name, "Metal")) break p;
    } else return error.SkipZigTest;

    var buf: [1024 * 8]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buf);
    try Material.serializePreset(metal, shader.pbr, &writer);

    var mat = try Material.loadFromBytes(a, writer.buffered());
    defer mat.deinit(a);
    try std.testing.expectEqual(@as(f32, 1.0), mat.scalar("metallic", 0));
    try std.testing.expectEqual(@as(f32, 0.15), mat.scalar("roughness", 0.5));
    try std.testing.expectEqual([4]f32{ 0.9, 0.9, 0.9, 1.0 }, mat.vector("base_color", .{ 0, 0, 0, 0 }));
}

test "preset Glass has alpha blend and no depth write" {
    const a = std.testing.allocator;
    const glass = for (Material.presets) |p| {
        if (std.mem.eql(u8, p.name, "Glass")) break p;
    } else return error.SkipZigTest;

    var buf: [1024 * 8]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buf);
    try Material.serializePreset(glass, shader.pbr, &writer);

    var mat = try Material.loadFromBytes(a, writer.buffered());
    defer mat.deinit(a);
    try std.testing.expectEqual(Material.BlendMode.alpha, mat.render.blend);
    try std.testing.expectEqual(false, mat.render.depth_write);
    try std.testing.expectEqual(Material.CullMode.none, mat.render.cull);
}
