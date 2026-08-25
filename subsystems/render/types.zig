//! Plain data types and constants shared across the renderer (no GPU state).
const engine = @import("engine");

/// Capacity of the per-frame lights storage buffer. Lights live in a storage
/// buffer (not the per-draw uniform), so this can be large without inflating
/// per-draw uniform pushes; the fragment shader loops only over the active count.
pub const MAX_LIGHTS = 256;

// Shadow mapping (primary directional light).
pub const SHADOW_DIM: u32 = 2048;

/// Cascaded shadow map split count. Each cascade is a vertical strip of the
/// shadow atlas texture (see `pipeline.createShadowMap`), fit to a slice of
/// the camera frustum.
pub const NUM_CASCADES: usize = 4;

/// Bytes for an asset resolved by GUID, plus whether the renderer frees them.
pub const Bytes = struct { data: []const u8, owned: bool = true };

/// GUID→bytes asset source. Meshes are canonical cooked bytes; textures are
/// encoded image bytes (PNG/KTX2/...); materials are `.material` JSON bytes.
pub const SourceFn = *const fn (guid: []const u8) ?Bytes;

/// Vertex-stage uniforms — model + model-view-projection.
pub const VertexUB = extern struct { mvp: [16]f32, model: [16]f32 };

/// Shadow-pass vertex uniforms — `light_vp * model`.
pub const ShadowUB = extern struct { light_mvp: [16]f32 };

/// Alpha-cutout shadow-pass fragment uniforms — layout must match FragUB in
/// shadow_mask.frag.glsl. Only enough of the material to replicate
/// scene.frag.glsl's alpha-cutout discard.
pub const ShadowMaskFragUB = extern struct {
    flags: [4]f32, // x=has_albedo, y=alpha_cutoff, zw unused
    base_color: [4]f32, // rgba
};

/// Depth+normal prepass vertex uniforms — layout must match VertexUB in
/// depth_normal.vert.glsl. `model_view = view * model`, composed on the CPU.
pub const PrepassVertexUB = extern struct { mvp: [16]f32, model_view: [16]f32 };

/// Depth+normal prepass fragment uniforms — layout must match FragUB in
/// depth_normal.frag.glsl. Only enough of the material to replicate
/// scene.frag.glsl's alpha-cutout discard for masked materials.
pub const PrepassFragUB = extern struct {
    flags: [4]f32, // x=has_albedo, y=alpha_cutoff, z=alpha_mask_on, w unused
    base_color: [4]f32, // rgba
};

/// SSAO pass fragment uniforms — layout must match FragUB in ssao.frag.glsl.
pub const SsaoUB = extern struct {
    inv_proj: [16]f32,
    proj: [16]f32,
    kernel_samples: [24][4]f32,
    params: [4]f32, // x=radius, y=bias, z=power, w unused
};

/// SSR pass fragment uniforms — layout must match FragUB in ssr.frag.glsl.
pub const SsrUB = extern struct {
    inv_proj: [16]f32,
    proj: [16]f32,
    /// `prev_view_proj * inverse(current_view)` — takes a view-space hit
    /// position directly to previous-frame clip space in one multiply.
    reproject: [16]f32,
    params: [4]f32, // x=thickness, y=step_scale, z=max_distance, w unused
};

/// One scene light. Layout must match `struct Light` in scene.frag.glsl.
pub const GpuLight = extern struct {
    position: [4]f32 = .{ 0, 0, 0, 0 }, // xyz world pos, w = type (0 dir,1 point,2 spot)
    direction: [4]f32 = .{ 0, -1, 0, 0 }, // xyz travel dir, w = range
    color: [4]f32 = .{ 0, 0, 0, 0 }, // rgb, w = intensity
    cone: [4]f32 = .{ -1, 1, 0, 0 }, // cos(outer), cos(inner)
};

/// Per-draw fragment uniforms — layout must match FragUB in scene.frag.glsl
/// exactly. All members are vec4 (or vec4 arrays) to keep std140 16-byte
/// alignment trivial. Scene lights are NOT here — they live in a separate
/// storage buffer bound once per pass (see `state.lights_buf`); frame-constant
/// values (SH irradiance, shadow cascades) are NOT here either — they live in
/// `FrameFragUB`, pushed once per pass — so this stays small per draw.
pub const FragUB = extern struct {
    camera_pos: [4]f32, // xyz, w = light_count
    base_color: [4]f32, // rgba
    mr_ns_oc: [4]f32, // metallic, roughness, normal_scale, occlusion_strength
    emissive: [4]f32, // rgb, w strength
    flags: [4]f32, // has_albedo, has_mr, has_normal, has_emissive
    flags2: [4]f32, // has_occlusion, alpha_cutoff, alpha_mask_on, shadows_enabled
    env_params: [4]f32, // x=intensity, y=mip_count, z=has_env, w unused
    cam_forward: [4]f32, // xyz camera forward (world space), w unused — picks the shadow cascade
    probe_params: [4]f32, // x=resolved reflection-probe weight, y=probe mip_count, zw unused
};

/// Frame-constant fragment uniforms — layout must match FrameFragUB in
/// scene.frag.glsl exactly. Pushed once per render pass (see
/// `draw.pushFrameUniforms`) instead of once per draw.
pub const FrameFragUB = extern struct {
    env_sh: [9][4]f32, // diffuse irradiance SH coefficients (rgb in xyz)
    cascade_vp: [NUM_CASCADES][16]f32, // per-cascade shadow light view-projection
    // Packed as a single vec4 in the shader (std140 array-of-float would pad
    // each element to 16 bytes) — one float per cascade, so NUM_CASCADES must stay 4.
    cascade_splits: [4]f32, // per-cascade far distance along cam_forward (world units)
    cascade_depth_scale: [4]f32, // per-cascade 1/(ortho far-near), converts a world-unit bias to NDC depth
};
comptime {
    if (NUM_CASCADES < 1 or NUM_CASCADES > 4) @compileError("FragUB packs cascade_splits/cascade_depth_scale into one vec4 each (max 4)");
}

/// Fragment uniforms for the skybox pass — layout must match FragUB in
/// skybox.frag.glsl exactly.
pub const SkyboxFragUB = extern struct {
    inv_view_proj: [16]f32,
    camera_pos_intensity: [4]f32, // xyz = camera world position, w = intensity
};

/// Post-process composite pass uniforms — layout must match FragUB in
/// composite.frag.glsl exactly. Grading is per-channel (RGB Lift/Gamma/Gain,
/// ASC CDL slope/offset/power form), matching Unity's own grading wheels.
pub const PostCompositeUB = extern struct {
    vignette: [4]f32, // x=intensity, y=radius, z=smoothness, w=aspect
    lift: [4]f32, // rgb, w unused
    gamma: [4]f32, // rgb, w unused
    gain: [4]f32, // rgb, w unused
    bloom: [4]f32, // x=bloom_intensity, yzw unused
};

/// Bloom threshold/downsample/upsample pass uniforms — layout must match
/// FragUB in the respective bloom_*.frag.glsl shaders.
pub const PostBloomUB = extern struct {
    params: [4]f32, // x=threshold, y=knee, z=radius (upsample only), w unused
};

/// Equirect->cubemap conversion pass uniforms — layout must match FragUB in
/// equirect_to_cubemap.frag.glsl exactly.
pub const EquirectToCubemapUB = extern struct {
    face: [4]f32, // x = face index (0..5), yzw unused
};

/// GGX-importance-sampled specular prefilter pass uniforms — layout must
/// match FragUB in prefilter_specular.frag.glsl exactly.
pub const PrefilterUB = extern struct {
    face_roughness: [4]f32, // x = face index (0..5), y = roughness, zw unused
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
