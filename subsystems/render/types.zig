//! Plain data types and constants shared across the renderer (no GPU state).
const engine = @import("engine");

pub const MAX_LIGHTS = 8;

// Shadow mapping (primary directional light).
pub const SHADOW_DIM: u32 = 2048;

/// Bytes for an asset resolved by GUID, plus whether the renderer frees them.
pub const Bytes = struct { data: []const u8, owned: bool = true };

/// GUID→bytes asset source. Meshes are canonical cooked bytes; textures are
/// encoded image bytes (PNG/KTX2/...); materials are `.material` JSON bytes.
pub const SourceFn = *const fn (guid: []const u8) ?Bytes;

/// Vertex-stage uniforms — model + model-view-projection.
pub const VertexUB = extern struct { mvp: [16]f32, model: [16]f32 };

/// Shadow-pass vertex uniforms — `light_vp * model`.
pub const ShadowUB = extern struct { light_mvp: [16]f32 };

/// One scene light. Layout must match `struct Light` in scene.frag.glsl.
pub const GpuLight = extern struct {
    position: [4]f32 = .{ 0, 0, 0, 0 }, // xyz world pos, w = type (0 dir,1 point,2 spot)
    direction: [4]f32 = .{ 0, -1, 0, 0 }, // xyz travel dir, w = range
    color: [4]f32 = .{ 0, 0, 0, 0 }, // rgb, w = intensity
    cone: [4]f32 = .{ -1, 1, 0, 0 }, // cos(outer), cos(inner)
};

/// Fragment uniforms — layout must match FragUB in scene.frag.glsl exactly.
/// All members are vec4 (or vec4 arrays) to keep std140 16-byte alignment trivial.
pub const FragUB = extern struct {
    camera_pos: [4]f32, // xyz, w = light_count
    base_color: [4]f32, // rgba
    mr_ns_oc: [4]f32, // metallic, roughness, normal_scale, occlusion_strength
    emissive: [4]f32, // rgb, w strength
    flags: [4]f32, // has_albedo, has_mr, has_normal, has_emissive
    flags2: [4]f32, // has_occlusion, alpha_cutoff, alpha_mask_on, shadows_enabled
    env_params: [4]f32, // x=intensity, y=mip_count, z=has_env, w unused
    env_sh: [9][4]f32, // diffuse irradiance SH coefficients (rgb in xyz)
    light_vp: [16]f32, // shadow light view-projection (primary directional)
    lights: [MAX_LIGHTS]GpuLight,
};

/// Fragment uniforms for the skybox pass — layout must match FragUB in
/// skybox.frag.glsl exactly.
pub const SkyboxFragUB = extern struct {
    inv_view_proj: [16]f32,
    camera_pos_intensity: [4]f32, // xyz = camera world position, w = intensity
};

/// One compute-readable submesh bounds entry for GPU-driven frustum culling.
/// Layout must match `SubmeshBounds` in cull.comp exactly — three vec4-sized
/// blocks, so std140 vs std430 alignment differences don't matter here.
pub const SubmeshBoundsGpu = extern struct {
    min: [4]f32 = .{ 0, 0, 0, 0 }, // xyz local-space min, w unused
    max: [4]f32 = .{ 0, 0, 0, 0 }, // xyz local-space max, w unused
    range: [4]u32 = .{ 0, 0, 0, 0 }, // x = first_index, y = num_indices, zw unused
};

/// Cull compute-shader uniforms — layout must match CullUB in cull.comp
/// exactly (std140: mat4 + vec4 array + uvec4 are all naturally 16-byte
/// aligned, so no hidden padding to account for).
pub const CullUB = extern struct {
    model: [16]f32,
    planes: [6][4]f32,
    submesh_count: [4]u32, // x = count, yzw unused
};

/// Vertex layout uploaded to the GPU (matches the pipeline's attributes).
pub const GpuVertex = extern struct {
    px: f32,
    py: f32,
    pz: f32,
    nx: f32,
    ny: f32,
    nz: f32,
    u: f32,
    v: f32,
};

/// PBR maps in shader binding order (set=2, binding 0..4).
pub const MapSlot = enum(usize) { albedo = 0, mr = 1, normal = 2, emissive = 3, occlusion = 4 };

/// A short string buffer holding a bound texture's GUID.
pub const GuidBuf = struct {
    buf: [64]u8 = undefined,
    len: usize = 0,

    pub fn slice(self: *const GuidBuf) []const u8 {
        return self.buf[0..self.len];
    }
    pub fn set(self: *GuidBuf, s: []const u8) void {
        self.len = @min(s.len, self.buf.len);
        @memcpy(self.buf[0..self.len], s[0..self.len]);
    }
};

/// A material flattened into scalar values and the GUIDs of its texture maps,
/// ready to drive the fragment uniforms and sampler bindings for one draw.
pub const ResolvedMaterial = struct {
    base_color: [4]f32 = .{ 0.72, 0.72, 0.76, 1.0 },
    metallic: f32 = 0,
    roughness: f32 = 0.5,
    normal_scale: f32 = 1,
    occlusion_strength: f32 = 1,
    emissive: [3]f32 = .{ 0, 0, 0 },
    emissive_strength: f32 = 0,
    alpha_cutoff: f32 = 0.5,
    maps: [5]GuidBuf = .{ .{}, .{}, .{}, .{}, .{} },
    /// Fixed-function state (blend/cull/depth/alpha-mask) from the material asset.
    render: engine.Material.RenderState = .{},

    pub fn map(self: *const ResolvedMaterial, slot: MapSlot) []const u8 {
        return self.maps[@intFromEnum(slot)].slice();
    }
};

pub const SceneNode = engine.SceneNode;

/// A free-look camera pose the editor imposes on the viewport, independent of
/// any scene camera component. When set, the renderer uses it instead of
/// scanning for a camera component. The shipped game never sets it.
pub const EditorCam = struct {
    pos: engine.Vector3,
    rot: engine.Vector3,
    fov: f32 = 60,
    near: f32 = 0.05,
    far: f32 = 2000,
    /// When > 0, the camera projects orthographically with this vertical
    /// half-extent (world units from center to top edge) instead of using a
    /// perspective `fov`. Asset previews use this so a model framed close up
    /// doesn't fan out under perspective foreshortening.
    ortho_half_height: f32 = 0,
};
