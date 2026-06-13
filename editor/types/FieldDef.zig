const std = @import("std");
const engine = @import("engine");

/// Maximum length of a reflected field name.
pub const MAX_FIELD_NAME_LEN = 64;

/// Describes a single field of a user script component.
pub const FieldDef = struct {
    name: [MAX_FIELD_NAME_LEN]u8 = std.mem.zeroes([MAX_FIELD_NAME_LEN]u8),
    name_len: usize = 0,
    kind: engine.api.FieldType = .f32,
    /// Asset category for `asset_ref` fields (drives the inspector picker).
    asset_filter: engine.api.AssetFilter = .any,
    // ── Scalar defaults ───────────────────────────────────────────────────────
    default_f32: f32 = 0,
    default_f64: f64 = 0,
    default_i32: i32 = 0,
    default_i64: i64 = 0,
    default_u32: u32 = 0,
    default_bool: bool = false,
    // ── Math vector defaults (flat components) ────────────────────────────────
    default_vec2_x: f32 = 0,
    default_vec2_y: f32 = 0,
    default_vec3_x: f32 = 0,
    default_vec3_y: f32 = 0,
    default_vec3_z: f32 = 0,
    default_vec4_x: f32 = 0,
    default_vec4_y: f32 = 0,
    default_vec4_z: f32 = 0,
    default_vec4_w: f32 = 0,

    pub fn nameSlice(self: *const @This()) []const u8 {
        return self.name[0..self.name_len];
    }

    pub fn setName(self: *@This(), n: []const u8) void {
        const len = @min(n.len, MAX_FIELD_NAME_LEN);
        @memcpy(self.name[0..len], n[0..len]);
        self.name_len = len;
    }
};
