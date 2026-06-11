/// C-ABI vec2 struct matching the engine's Vector2 layout.
pub const Vec2Value = extern struct {
    x: f32 = 0,
    y: f32 = 0,
};

/// C-ABI vec3 struct matching the engine's Vector3 layout.
pub const Vec3Value = extern struct {
    x: f32 = 0,
    y: f32 = 0,
    z: f32 = 0,
};

/// C-ABI vec4 struct matching the engine's Vector4 layout.
pub const Vec4Value = extern struct {
    x: f32 = 0,
    y: f32 = 0,
    z: f32 = 0,
    w: f32 = 0,
};

/// C-ABI tagged union of supported field value types.
/// Size is determined by the largest variant (Vec4Value = 16 bytes).
pub const FieldValue = extern union {
    as_f32: f32,
    as_f64: f64,
    as_i32: i32,
    as_i64: i64,
    as_u32: u32,
    as_bool: bool,
    as_vec2: Vec2Value,
    as_vec3: Vec3Value,
    as_vec4: Vec4Value,
};
