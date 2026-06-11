/// Supported data types for reflected component fields.
/// The first seven variants are stable and must not be reordered (C-ABI shared libraries depend on
/// the integer values). New variants may only be appended.
pub const FieldType = enum(u32) {
    // ── Stable set (C-ABI guaranteed order) ──────────────────────────────────
    f32,
    i32,
    bool,
    vec3,
    game_object_ref,
    component_ref,
    asset_ref,
    // ── Extended scalar widths ────────────────────────────────────────────────
    f64,
    i64,
    u32,
    // ── Extended math types ───────────────────────────────────────────────────
    vec2,
    vec4,
    // ── String (null-terminated fixed buffer) ─────────────────────────────────
    string,
};
