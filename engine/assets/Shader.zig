//! Shader metadata system. A shader exposes named parameters, driving
//! default material values, inspector UI, and the shader-node contract.
//! Built-in shaders use stable GUIDs; the engine falls back to PBR for
//! unresolvable shaders.
const std = @import("std");

/// Value category of a shader parameter. Determines how the value is stored in
/// a material and which widget the inspector draws for it.
pub const ParamKind = enum {
    /// Single float.
    scalar,
    /// 2-component vector (stored in xy of the value).
    vec2,
    /// 3-component vector (stored in xyz of the value).
    vec3,
    /// 4-component vector.
    vec4,
    /// RGBA colour — same storage as vec4 but edited with a colour picker.
    color,
    /// Texture binding, referenced by asset GUID.
    texture,
};

/// A single parameter exposed by a shader.
pub const ShaderParam = struct {
    /// Stable identifier / uniform name (snake_case). Material values are keyed
    /// by this string, so it must remain stable across shader revisions.
    name: []const u8,
    /// Human-readable label shown in the inspector.
    label: []const u8,
    /// Value category — drives storage and inspector widget.
    kind: ParamKind,
    /// Default value for `kind == .scalar`.
    default_scalar: f32 = 0,
    /// Default value for vector/colour kinds (RGBA / XYZW).
    default_vec: [4]f32 = .{ 0, 0, 0, 1 },
    /// Inclusive minimum for a bounded scalar slider.
    min: f32 = 0,
    /// Inclusive maximum for a bounded scalar slider.
    max: f32 = 1,
    /// When true the inspector draws a bounded slider using `min`/`max`,
    /// otherwise a plain numeric entry.
    ranged: bool = false,
};

/// Describes the parameters a shader exposes, in inspector display order.
pub const ShaderDef = struct {
    /// Stable asset GUID identifying this shader.
    guid: []const u8,
    /// Display name.
    name: []const u8,
    /// Exposed parameters.
    params: []const ShaderParam,

    /// Look up a parameter by its identifier, or null if the shader has none.
    pub fn findParam(self: ShaderDef, name: []const u8) ?ShaderParam {
        for (self.params) |p| {
            if (std.mem.eql(u8, p.name, name)) return p;
        }
        return null;
    }
};

// ── Built-in shaders ──────────────────────────────────────────────────────────

/// Stable GUID for the built-in metallic-roughness PBR shader. Materials default
/// to this shader. Treat it as a constant — changing it orphans existing assets.
pub const pbr_guid = "00000000-0000-4000-8000-000000000001";

/// Built-in physically based (metallic-roughness) shader. Mirrors the glTF 2.0
/// metallic-roughness material model so imported glTF materials map 1:1.
pub const pbr = ShaderDef{
    .guid = pbr_guid,
    .name = "PBR (Metallic-Roughness)",
    .params = &.{
        .{ .name = "base_color", .label = "Base Color", .kind = .color, .default_vec = .{ 1, 1, 1, 1 } },
        .{ .name = "metallic", .label = "Metallic", .kind = .scalar, .default_scalar = 0.0, .min = 0, .max = 1, .ranged = true },
        .{ .name = "roughness", .label = "Roughness", .kind = .scalar, .default_scalar = 0.5, .min = 0, .max = 1, .ranged = true },
        .{ .name = "emissive", .label = "Emissive", .kind = .color, .default_vec = .{ 0, 0, 0, 1 } },
        .{ .name = "emissive_strength", .label = "Emissive Strength", .kind = .scalar, .default_scalar = 0.0, .min = 0, .max = 10, .ranged = true },
        .{ .name = "normal_scale", .label = "Normal Scale", .kind = .scalar, .default_scalar = 1.0, .min = 0, .max = 2, .ranged = true },
        .{ .name = "occlusion_strength", .label = "Occlusion", .kind = .scalar, .default_scalar = 1.0, .min = 0, .max = 1, .ranged = true },
        .{ .name = "alpha_cutoff", .label = "Alpha Cutoff", .kind = .scalar, .default_scalar = 0.5, .min = 0, .max = 1, .ranged = true },
        .{ .name = "albedo_map", .label = "Albedo Map", .kind = .texture },
        .{ .name = "metallic_roughness_map", .label = "Metallic/Roughness Map", .kind = .texture },
        .{ .name = "normal_map", .label = "Normal Map", .kind = .texture },
        .{ .name = "emissive_map", .label = "Emissive Map", .kind = .texture },
        .{ .name = "occlusion_map", .label = "Occlusion Map", .kind = .texture },
    },
};

/// All shaders compiled into the engine.
pub const builtins = [_]ShaderDef{pbr};

/// Look up a built-in shader by GUID, or null if none matches.
pub fn builtin(guid: []const u8) ?ShaderDef {
    for (builtins) |s| {
        if (std.mem.eql(u8, s.guid, guid)) return s;
    }
    return null;
}

/// The shader new materials use and the fallback when a reference cannot be
/// resolved (currently PBR).
pub fn default() ShaderDef {
    return pbr;
}

/// Resolve a shader GUID to its definition, falling back to the default shader
/// when the GUID is empty or not a known built-in. Custom on-disk shaders are
/// resolved by the editor; the engine only knows builtins.
pub fn resolve(guid: []const u8) ShaderDef {
    if (guid.len == 0) return default();
    return builtin(guid) orelse default();
}

// ── Tests ─────────────────────────────────────────────────────────────────────

test "resolve falls back to PBR for unknown or empty guids" {
    try std.testing.expectEqualStrings(pbr_guid, resolve("").guid);
    try std.testing.expectEqualStrings(pbr_guid, resolve("not-a-real-guid").guid);
    try std.testing.expectEqualStrings(pbr_guid, resolve(pbr_guid).guid);
}

test "pbr exposes the metallic-roughness parameter set" {
    const m = pbr.findParam("metallic") orelse return error.MissingParam;
    try std.testing.expectEqual(ParamKind.scalar, m.kind);
    try std.testing.expect(m.ranged);

    const bc = pbr.findParam("base_color") orelse return error.MissingParam;
    try std.testing.expectEqual(ParamKind.color, bc.kind);

    try std.testing.expect(pbr.findParam("albedo_map") != null);
    try std.testing.expect(pbr.findParam("nonexistent") == null);
}
