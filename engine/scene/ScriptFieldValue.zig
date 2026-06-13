const std = @import("std");
const api = @import("../api/root.zig");

/// Maximum length of a script field name.
pub const MAX_FIELD_NAME = 64;

/// Maximum byte length of a script string field (null-terminated).
pub const MAX_SCRIPT_STRING = 128;

/// A single serialised field value for a user script component.
pub const ScriptFieldValue = struct {
    name: [MAX_FIELD_NAME]u8 = std.mem.zeroes([MAX_FIELD_NAME]u8),
    name_len: usize = 0,
    /// The data type of this field.
    kind: api.FieldType = .f32,
    /// Asset category for `asset_ref` fields (drives the inspector picker).
    /// Restored from reflection — not persisted in the scene file.
    asset_filter: api.AssetFilter = .any,
    // ── Numeric scalars ───────────────────────────────────────────────────────
    as_f32: f32 = 0,
    as_f64: f64 = 0,
    as_i32: i32 = 0,
    as_i64: i64 = 0,
    as_u32: u32 = 0,
    as_bool: bool = false,
    // ── Math vectors (stored as flat components) ──────────────────────────────
    as_vec2_x: f32 = 0,
    as_vec2_y: f32 = 0,
    as_vec3_x: f32 = 0,
    as_vec3_y: f32 = 0,
    as_vec3_z: f32 = 0,
    as_vec4_x: f32 = 0,
    as_vec4_y: f32 = 0,
    as_vec4_z: f32 = 0,
    as_vec4_w: f32 = 0,
    // ── Reference (game object / component / asset GUID) ──────────────────────
    as_ref: [api.MAX_REF_LEN]u8 = std.mem.zeroes([api.MAX_REF_LEN]u8),
    as_ref_len: usize = 0,
    // ── String (null-terminated fixed buffer) ─────────────────────────────────
    as_string: [MAX_SCRIPT_STRING]u8 = std.mem.zeroes([MAX_SCRIPT_STRING]u8),

    /// Returns the field name as a slice.
    pub fn nameSlice(self: *const @This()) []const u8 {
        return self.name[0..self.name_len];
    }

    /// Sets the field name, truncating if necessary.
    pub fn setName(self: *@This(), n: []const u8) void {
        const len = @min(n.len, MAX_FIELD_NAME);
        @memcpy(self.name[0..len], n[0..len]);
        self.name_len = len;
    }

    /// Returns the reference value as a slice.
    pub fn refSlice(self: *const @This()) []const u8 {
        return self.as_ref[0..self.as_ref_len];
    }

    /// Sets the reference value, truncating if necessary.
    pub fn setRef(self: *@This(), r: []const u8) void {
        const len = @min(r.len, api.MAX_REF_LEN);
        @memcpy(self.as_ref[0..len], r[0..len]);
        @memset(self.as_ref[len..], 0);
        self.as_ref_len = len;
    }

    /// Returns the string value as a null-terminated slice.
    pub fn stringSlice(self: *const @This()) []const u8 {
        const end = std.mem.indexOfScalar(u8, &self.as_string, 0) orelse MAX_SCRIPT_STRING;
        return self.as_string[0..end];
    }

    /// Writes a string into the fixed buffer, null-terminating and zero-padding.
    pub fn setString(self: *@This(), s: []const u8) void {
        const len = @min(s.len, MAX_SCRIPT_STRING - 1);
        @memcpy(self.as_string[0..len], s[0..len]);
        @memset(self.as_string[len..], 0);
    }
};
