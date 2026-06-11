const engine = @import("engine");

/// Serialisable user script field value for JSON persistence.
pub const SceneScriptField = struct {
    name: []const u8 = "",
    kind: engine.api.FieldType = .f32,
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
    /// Reference value as a stable GUID string (UUID format). Empty if none.
    as_ref_guid: []const u8 = "",
    /// String value. Empty if none.
    as_string: []const u8 = "",
};
