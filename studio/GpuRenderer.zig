/// Hardware 3D renderer for the editor scene viewport.
/// Uses SDL3GPU (Vulkan/Metal/D3D12) to render the current scene
/// into an offscreen texture displayed by DVUI.
const std = @import("std");
const dvui = @import("dvui");
const engine = @import("engine");
const EditorState = @import("EditorState.zig");

const SDLBackend = dvui.backend.SDLBackend;
const c = dvui.backend.c;
const Matrix4 = engine.Matrix4;
const Vector3 = engine.Vector3;

var g_device: ?*c.SDL_GPUDevice = null;
var g_backend: ?*SDLBackend = null;
var g_cmd: ?*c.SDL_GPUCommandBuffer = null;
var g_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
var g_sampler: ?*c.SDL_GPUSampler = null;

// Shadow mapping (primary directional light).
const SHADOW_DIM: u32 = 2048;
const SHADOW_FORMAT = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM;
var g_shadow_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
var g_shadow_map: ?*c.SDL_GPUTexture = null;
var g_shadow_sampler: ?*c.SDL_GPUSampler = null;

var g_color_target: ?dvui.TextureTarget = null;
var g_depth_tex: ?*c.SDL_GPUTexture = null;
var g_target_w: u32 = 0;
var g_target_h: u32 = 0;

var g_white_tex: ?*c.SDL_GPUTexture = null;
// Default tangent-space "flat" normal (points straight out): rgb (128,128,255).
var g_flat_normal_tex: ?*c.SDL_GPUTexture = null;

const MAX_MESHES = 32;
const GpuMesh = struct {
    path: [256]u8 = undefined,
    path_len: usize = 0,
    vtx_buf: *c.SDL_GPUBuffer = undefined,
    idx_buf: *c.SDL_GPUBuffer = undefined,
    idx_count: u32 = 0,

    fn matchesPath(self: *const @This(), p: []const u8) bool {
        return std.mem.eql(u8, self.path[0..self.path_len], p);
    }
};
var g_meshes: [MAX_MESHES]GpuMesh = undefined;
var g_mesh_count: usize = 0;

const MAX_TEXTURES = 32;
const GpuTexture = struct {
    path: [256]u8 = undefined,
    path_len: usize = 0,
    texture: *c.SDL_GPUTexture = undefined,

    fn matchesPath(self: *const @This(), p: []const u8) bool {
        return std.mem.eql(u8, self.path[0..self.path_len], p);
    }
};
var g_textures: [MAX_TEXTURES]GpuTexture = undefined;
var g_texture_count: usize = 0;

const VertexUB = extern struct { mvp: [16]f32, model: [16]f32 };

/// Shadow-pass vertex uniforms — `light_vp * model`.
const ShadowUB = extern struct { light_mvp: [16]f32 };

const MAX_LIGHTS = 8;

/// One scene light. Layout must match `struct Light` in scene.frag.glsl.
const GpuLight = extern struct {
    position: [4]f32 = .{ 0, 0, 0, 0 }, // xyz world pos, w = type (0 dir,1 point,2 spot)
    direction: [4]f32 = .{ 0, -1, 0, 0 }, // xyz travel dir, w = range
    color: [4]f32 = .{ 0, 0, 0, 0 }, // rgb, w = intensity
    cone: [4]f32 = .{ -1, 1, 0, 0 }, // cos(outer), cos(inner)
};

/// Fragment uniforms — layout must match FragUB in scene.frag.glsl exactly.
/// All members are vec4 (or vec4 arrays) to keep std140 16-byte alignment trivial.
const FragUB = extern struct {
    ambient_color: [4]f32, // rgb
    camera_pos: [4]f32, // xyz, w = light_count
    base_color: [4]f32, // rgba
    mr_ns_oc: [4]f32, // metallic, roughness, normal_scale, occlusion_strength
    emissive: [4]f32, // rgb, w strength
    flags: [4]f32, // has_albedo, has_mr, has_normal, has_emissive
    flags2: [4]f32, // has_occlusion, alpha_cutoff, alpha_mask_on, shadows_enabled
    light_vp: [16]f32, // shadow light view-projection (primary directional)
    lights: [MAX_LIGHTS]GpuLight,
};

/// PBR maps in shader binding order (set=2, binding 0..4).
const MapSlot = enum(usize) { albedo = 0, mr = 1, normal = 2, emissive = 3, occlusion = 4 };

/// A resolved texture asset path (full path as stored in the AssetDatabase).
const PathBuf = struct {
    buf: [512]u8 = undefined,
    len: usize = 0,

    fn slice(self: *const PathBuf) []const u8 {
        return self.buf[0..self.len];
    }
    fn set(self: *PathBuf, s: []const u8) void {
        self.len = @min(s.len, self.buf.len);
        @memcpy(self.buf[0..self.len], s[0..self.len]);
    }
};

/// A material flattened into scalar values and resolved texture paths, ready to
/// drive the fragment uniforms and sampler bindings for one draw.
const ResolvedMaterial = struct {
    base_color: [4]f32 = .{ 0.72, 0.72, 0.76, 1.0 },
    metallic: f32 = 0,
    roughness: f32 = 0.5,
    normal_scale: f32 = 1,
    occlusion_strength: f32 = 1,
    emissive: [3]f32 = .{ 0, 0, 0 },
    emissive_strength: f32 = 0,
    alpha_cutoff: f32 = 0.5,
    maps: [5]PathBuf = .{ .{}, .{}, .{}, .{}, .{} },

    fn map(self: *const ResolvedMaterial, slot: MapSlot) []const u8 {
        return self.maps[@intFromEnum(slot)].slice();
    }
};

const GpuVertex = extern struct {
    px: f32,
    py: f32,
    pz: f32,
    nx: f32,
    ny: f32,
    nz: f32,
    u: f32,
    v: f32,
};

const BackendTex = extern struct {
    texture: *c.SDL_GPUTexture,
    sampler: *c.SDL_GPUSampler,
};

/// Initialize the GPU renderer with the SDL backend.
pub fn init(backend: *SDLBackend) !void {
    g_backend = backend;
    g_device = backend.device;
    if (backend.shaderformat != c.SDL_GPU_SHADERFORMAT_SPIRV) {
        std.debug.print("[GpuRenderer] Non-SPIRV backend – 3D viewport disabled.\n", .{});
        return;
    }
    g_pipeline = try createPipeline(backend.device);
    g_sampler = try createSampler(backend.device);
    g_shadow_sampler = createShadowSampler(backend.device) catch |err| s: {
        std.debug.print("[GpuRenderer] shadow sampler failed: {any} — shadows disabled.\n", .{err});
        break :s null;
    };
    g_shadow_pipeline = createShadowPipeline(backend.device) catch |err| p: {
        std.debug.print("[GpuRenderer] shadow pipeline failed: {any} — shadows disabled.\n", .{err});
        break :p null;
    };
    std.debug.print("[GpuRenderer] Ready (SPIRV).\n", .{});
}

/// Begin a new GPU frame for rendering.
pub fn beginFrame(cmd: ?*c.SDL_GPUCommandBuffer) void {
    g_cmd = cmd;
}

/// Render the 3D scene into an offscreen texture for the viewport.
pub fn renderViewport(w: u32, h: u32) ?dvui.TextureTarget {
    const cmd = g_cmd orelse return null;
    const pipeline = g_pipeline orelse return null;
    const dev = g_device orelse return null;
    const backend = g_backend orelse return null;
    const sampler = g_sampler orelse return null;
    if (w == 0 or h == 0) return null;

    if (g_white_tex == null) {
        g_white_tex = createSolidTexture(cmd, dev, .{ 255, 255, 255, 255 }) catch |err| n: {
            std.debug.print("[GpuRenderer] white texture failed: {any}\n", .{err});
            break :n null;
        };
    }
    if (g_flat_normal_tex == null) {
        g_flat_normal_tex = createSolidTexture(cmd, dev, .{ 128, 128, 255, 255 }) catch |err| n: {
            std.debug.print("[GpuRenderer] flat-normal texture failed: {any}\n", .{err});
            break :n null;
        };
    }
    if (g_shadow_map == null) {
        g_shadow_map = createShadowMap(dev) catch |err| n: {
            std.debug.print("[GpuRenderer] shadow map failed: {any} — shadows disabled.\n", .{err});
            break :n null;
        };
    }

    if (w != g_target_w or h != g_target_h) {
        destroyTargets(dev);
        createTargets(backend, dev, w, h) catch |err| {
            std.debug.print("[GpuRenderer] createTargets failed: {any}\n", .{err});
            return null;
        };
        g_target_w = w;
        g_target_h = h;
    }

    const color_target = g_color_target orelse return null;
    const depth_tex = g_depth_tex orelse return null;
    const bt: *BackendTex = @ptrCast(@alignCast(color_target.ptr));

    const objects = EditorState.objects[0..EditorState.object_count];
    uploadNewAssets(cmd, dev, objects);

    var cam_pos = Vector3{ .x = 0, .y = 2, .z = -5 };
    var cam_rot = Vector3{};
    var cam_fov: f32 = 60.0;
    var cam_near: f32 = 0.01;
    var cam_far: f32 = 1000.0;
    cam_search: for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* == .camera) {
                cam_pos = obj.transform.position;
                cam_rot = obj.transform.rotation;
                cam_fov = comp.camera.fov;
                cam_near = comp.camera.near;
                cam_far = comp.camera.far;
                break :cam_search;
            }
        }
    }

    const rm = Matrix4.rotationEuler(cam_rot.x, cam_rot.y, cam_rot.z);
    const fwd_v = rm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
    const look_at = Vector3{ .x = cam_pos.x + fwd_v.x, .y = cam_pos.y + fwd_v.y, .z = cam_pos.z + fwd_v.z };
    const view = Matrix4.lookAt(cam_pos, look_at, .{ .x = 0, .y = 1, .z = 0 });
    const asp = @as(f32, @floatFromInt(w)) / @as(f32, @floatFromInt(h));
    const proj = Matrix4.perspective(cam_fov, asp, cam_near, cam_far);
    const vp = proj.multiply(view);

    const ambient = [4]f32{ 0.15, 0.15, 0.18, 0.0 };

    // Gather up to MAX_LIGHTS scene lights. The first shadow-casting directional
    // light (kept at slot 0) drives the shadow map.
    var lights = [_]GpuLight{.{}} ** MAX_LIGHTS;
    var light_count: usize = 0;
    var shadow_dir: ?Vector3 = null;
    for (objects) |*obj| {
        if (!obj.active or light_count >= MAX_LIGHTS) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .light) continue;
            const lc = &comp.light;
            const lrm = Matrix4.rotationEuler(obj.transform.rotation.x, obj.transform.rotation.y, obj.transform.rotation.z);
            const d = lrm.transformDirection(.{ .x = 0, .y = 0, .z = 1 });
            const ty: f32 = switch (lc.kind) {
                .directional => 0,
                .point => 1,
                .spot => 2,
            };
            const p = obj.transform.position;
            const cos_outer = @cos(lc.spot_angle * std.math.pi / 180.0);
            const cos_inner = @cos(lc.spot_angle * (1.0 - lc.spot_softness) * std.math.pi / 180.0);

            // The shadow-casting directional light belongs at slot 0 so the
            // shader (which only shadows light 0) shadows the right one.
            const slot = if (lc.kind == .directional and lc.cast_shadows and shadow_dir == null) blk: {
                shadow_dir = .{ .x = d.x, .y = d.y, .z = d.z };
                if (light_count > 0) lights[light_count] = lights[0];
                break :blk 0;
            } else light_count;

            lights[slot] = .{
                .position = .{ p.x, p.y, p.z, ty },
                .direction = .{ d.x, d.y, d.z, lc.range },
                .color = .{ lc.color_r, lc.color_g, lc.color_b, lc.intensity },
                .cone = .{ cos_outer, cos_inner, 0, 0 },
            };
            light_count += 1;
        }
    }

    // Light-space matrix for the shadow map: an orthographic box fit around the
    // scene's mesh bounds, looking along the primary directional light.
    const bounds = sceneBounds(objects);
    const light_vp = if (shadow_dir) |sd| shadowMatrix(sd, bounds) else Matrix4{};
    const shadows_on = shadow_dir != null and g_shadow_map != null and g_shadow_sampler != null and g_shadow_pipeline != null;

    if (shadows_on) renderShadowPass(cmd, light_vp, objects);

    var color_info = std.mem.zeroes(c.SDL_GPUColorTargetInfo);
    color_info.texture = bt.texture;
    color_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    color_info.store_op = c.SDL_GPU_STOREOP_STORE;
    color_info.clear_color = .{ .r = 0.14, .g = 0.14, .b = 0.16, .a = 1.0 };

    var depth_info = std.mem.zeroes(c.SDL_GPUDepthStencilTargetInfo);
    depth_info.texture = depth_tex;
    depth_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    depth_info.store_op = c.SDL_GPU_STOREOP_DONT_CARE;
    depth_info.clear_depth = 1.0;

    const pass = c.SDL_BeginGPURenderPass(cmd, &color_info, 1, &depth_info) orelse return color_target;
    c.SDL_BindGPUGraphicsPipeline(pass, pipeline);
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(w),
        .h = @floatFromInt(h),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });

    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            const guid_str = comp.mesh_renderer.mesh.slice();
            if (guid_str.len == 0) continue;
            const mp = EditorState.resolveAssetGuid(guid_str) orelse continue;
            const gm = findGpuMesh(mp) orelse continue;

            const t = &obj.transform;
            const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
                .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
                .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
            const mvp = vp.multiply(mdl);

            const vub = VertexUB{ .mvp = mvp.m, .model = mdl.m };
            c.SDL_PushGPUVertexUniformData(cmd, 0, &vub, @sizeOf(VertexUB));

            const mat_res = resolveMaterial(comp.mesh_renderer.material.slice());

            const white = g_white_tex orelse continue;
            const flat_n = g_flat_normal_tex orelse white;
            const albedo_t = pickTexture(mat_res.map(.albedo), white);
            const mr_t = pickTexture(mat_res.map(.mr), white);
            const normal_t = pickTexture(mat_res.map(.normal), flat_n);
            const emis_t = pickTexture(mat_res.map(.emissive), white);
            const occ_t = pickTexture(mat_res.map(.occlusion), white);

            const receives = comp.mesh_renderer.receive_shadows and shadows_on;
            const fub = FragUB{
                .ambient_color = ambient,
                .camera_pos = .{ cam_pos.x, cam_pos.y, cam_pos.z, @floatFromInt(light_count) },
                .base_color = mat_res.base_color,
                .mr_ns_oc = .{ mat_res.metallic, mat_res.roughness, mat_res.normal_scale, mat_res.occlusion_strength },
                .emissive = .{ mat_res.emissive[0], mat_res.emissive[1], mat_res.emissive[2], mat_res.emissive_strength },
                .flags = .{ present(albedo_t.found), present(mr_t.found), present(normal_t.found), present(emis_t.found) },
                .flags2 = .{ present(occ_t.found), mat_res.alpha_cutoff, 0.0, present(receives) },
                .light_vp = light_vp.m,
                .lights = lights,
            };
            c.SDL_PushGPUFragmentUniformData(cmd, 0, &fub, @sizeOf(FragUB));

            const shadow_tex = g_shadow_map orelse white;
            const shadow_smp = g_shadow_sampler orelse sampler;
            const bindings = [_]c.SDL_GPUTextureSamplerBinding{
                .{ .texture = albedo_t.tex, .sampler = sampler },
                .{ .texture = mr_t.tex, .sampler = sampler },
                .{ .texture = normal_t.tex, .sampler = sampler },
                .{ .texture = emis_t.tex, .sampler = sampler },
                .{ .texture = occ_t.tex, .sampler = sampler },
                .{ .texture = shadow_tex, .sampler = shadow_smp },
            };
            c.SDL_BindGPUFragmentSamplers(pass, 0, &bindings, 6);

            c.SDL_BindGPUVertexBuffers(pass, 0, &c.SDL_GPUBufferBinding{ .buffer = gm.vtx_buf, .offset = 0 }, 1);
            c.SDL_BindGPUIndexBuffer(pass, &c.SDL_GPUBufferBinding{ .buffer = gm.idx_buf, .offset = 0 }, c.SDL_GPU_INDEXELEMENTSIZE_32BIT);
            c.SDL_DrawGPUIndexedPrimitives(pass, gm.idx_count, 1, 0, 0, 0);
        }
    }

    c.SDL_EndGPURenderPass(pass);
    return color_target;
}

/// Release all GPU resources and clean up.
pub fn deinit() void {
    const dev = g_device orelse return;
    destroyTargets(dev);
    if (g_white_tex) |t| {
        c.SDL_ReleaseGPUTexture(dev, t);
        g_white_tex = null;
    }
    if (g_flat_normal_tex) |t| {
        c.SDL_ReleaseGPUTexture(dev, t);
        g_flat_normal_tex = null;
    }
    for (g_textures[0..g_texture_count]) |*gt|
        c.SDL_ReleaseGPUTexture(dev, gt.texture);
    g_texture_count = 0;
    for (g_meshes[0..g_mesh_count]) |*gm| {
        c.SDL_ReleaseGPUBuffer(dev, gm.vtx_buf);
        c.SDL_ReleaseGPUBuffer(dev, gm.idx_buf);
    }
    g_mesh_count = 0;
    if (g_shadow_map) |t| c.SDL_ReleaseGPUTexture(dev, t);
    if (g_shadow_sampler) |s| c.SDL_ReleaseGPUSampler(dev, s);
    if (g_shadow_pipeline) |p| c.SDL_ReleaseGPUGraphicsPipeline(dev, p);
    g_shadow_map = null;
    g_shadow_sampler = null;
    g_shadow_pipeline = null;
    if (g_sampler) |s| c.SDL_ReleaseGPUSampler(dev, s);
    if (g_pipeline) |p| c.SDL_ReleaseGPUGraphicsPipeline(dev, p);
    g_sampler = null;
    g_pipeline = null;
}

fn createSampler(dev: *c.SDL_GPUDevice) !*c.SDL_GPUSampler {
    return c.SDL_CreateGPUSampler(dev, &c.SDL_GPUSamplerCreateInfo{
        .min_filter = c.SDL_GPU_FILTER_LINEAR,
        .mag_filter = c.SDL_GPU_FILTER_LINEAR,
        .mipmap_mode = c.SDL_GPU_SAMPLERMIPMAPMODE_LINEAR,
        .address_mode_u = c.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
        .address_mode_v = c.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
        .address_mode_w = c.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
        .mip_lod_bias = 0,
        .max_anisotropy = 1,
        .compare_op = c.SDL_GPU_COMPAREOP_NEVER,
        .min_lod = 0,
        .max_lod = 1000,
        .enable_anisotropy = false,
        .enable_compare = false,
        .padding1 = 0,
        .padding2 = 0,
        .props = 0,
    }) orelse error.SamplerCreate;
}

/// Create a 1x1 RGBA texture used as a sampler default for unbound material maps.
fn createSolidTexture(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, rgba: [4]u8) !*c.SDL_GPUTexture {
    const tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = 4,
        .props = 0,
    }) orelse return error.TransferCreate;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, tb);

    const p: [*]u8 = @ptrCast(@alignCast(
        c.SDL_MapGPUTransferBuffer(dev, tb, false) orelse return error.MapFailed,
    ));
    p[0] = rgba[0];
    p[1] = rgba[1];
    p[2] = rgba[2];
    p[3] = rgba[3];
    c.SDL_UnmapGPUTransferBuffer(dev, tb);

    const tex = c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER,
        .width = 1,
        .height = 1,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse return error.TextureCreate;

    const cp = c.SDL_BeginGPUCopyPass(cmd) orelse return error.CopyPassFailed;
    c.SDL_UploadToGPUTexture(cp, &c.SDL_GPUTextureTransferInfo{ .transfer_buffer = tb, .offset = 0, .pixels_per_row = 1, .rows_per_layer = 1 }, &c.SDL_GPUTextureRegion{ .texture = tex, .mip_level = 0, .layer = 0, .x = 0, .y = 0, .z = 0, .w = 1, .h = 1, .d = 1 }, false);
    c.SDL_EndGPUCopyPass(cp);
    return tex;
}

fn createTargets(backend: *SDLBackend, dev: *c.SDL_GPUDevice, w: u32, h: u32) !void {
    g_color_target = try backend.textureCreateTarget(.{ .width = w, .height = h });
    g_depth_tex = c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM,
        .usage = c.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse return error.DepthTextureCreate;
}

fn destroyTargets(dev: *c.SDL_GPUDevice) void {
    if (g_depth_tex) |dt| {
        c.SDL_ReleaseGPUTexture(dev, dt);
        g_depth_tex = null;
    }
    if (g_color_target) |ct| {
        if (g_backend) |b| b.textureDestroyTarget(ct);
        g_color_target = null;
    }
    g_target_w = 0;
    g_target_h = 0;
}

// ── Shadow mapping ────────────────────────────────────────────────────────────

const Bounds = struct { center: Vector3, radius: f32 };

/// Axis-aligned bounds of all active mesh-renderer object positions, with a
/// margin for mesh extent. Used to fit the directional shadow frustum.
fn sceneBounds(objects: []const engine.SceneNode) Bounds {
    var min = Vector3{ .x = 1e30, .y = 1e30, .z = 1e30 };
    var max = Vector3{ .x = -1e30, .y = -1e30, .z = -1e30 };
    var any = false;
    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            const p = obj.transform.position;
            // Approximate mesh half-extent from the object scale.
            const t = &obj.transform;
            const e = @max(@abs(t.scale.x), @max(@abs(t.scale.y), @abs(t.scale.z)));
            min = .{ .x = @min(min.x, p.x - e), .y = @min(min.y, p.y - e), .z = @min(min.z, p.z - e) };
            max = .{ .x = @max(max.x, p.x + e), .y = @max(max.y, p.y + e), .z = @max(max.z, p.z + e) };
            any = true;
        }
    }
    if (!any) return .{ .center = .{ .x = 0, .y = 0, .z = 0 }, .radius = 10 };
    const center = Vector3{ .x = (min.x + max.x) * 0.5, .y = (min.y + max.y) * 0.5, .z = (min.z + max.z) * 0.5 };
    const dx = max.x - center.x;
    const dy = max.y - center.y;
    const dz = max.z - center.z;
    const radius = @max(1.0, @sqrt(dx * dx + dy * dy + dz * dz));
    return .{ .center = center, .radius = radius };
}

/// Build the orthographic light view-projection covering `bounds`, looking
/// along `dir` (the directional light's travel direction).
fn shadowMatrix(dir: Vector3, bounds: Bounds) Matrix4 {
    const dl = @sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
    const d = if (dl > 1e-6) Vector3{ .x = dir.x / dl, .y = dir.y / dl, .z = dir.z / dl } else Vector3{ .x = 0, .y = -1, .z = 0 };
    const r = bounds.radius;
    const eye = Vector3{ .x = bounds.center.x - d.x * r * 2.0, .y = bounds.center.y - d.y * r * 2.0, .z = bounds.center.z - d.z * r * 2.0 };
    // Pick an up vector not parallel to the light direction.
    const up = if (@abs(d.y) > 0.99) Vector3{ .x = 0, .y = 0, .z = 1 } else Vector3{ .x = 0, .y = 1, .z = 0 };
    const view = Matrix4.lookAt(eye, bounds.center, up);
    const ortho = Matrix4.orthographic(-r, r, -r, r, 0.01, r * 4.0 + 1.0);
    return ortho.multiply(view);
}

fn createShadowMap(dev: *c.SDL_GPUDevice) !*c.SDL_GPUTexture {
    return c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = SHADOW_FORMAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
        .width = SHADOW_DIM,
        .height = SHADOW_DIM,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse error.ShadowMapCreate;
}

/// Depth-comparison sampler for PCF shadow lookups (sampler2DShadow).
fn createShadowSampler(dev: *c.SDL_GPUDevice) !*c.SDL_GPUSampler {
    return c.SDL_CreateGPUSampler(dev, &c.SDL_GPUSamplerCreateInfo{
        .min_filter = c.SDL_GPU_FILTER_LINEAR,
        .mag_filter = c.SDL_GPU_FILTER_LINEAR,
        .mipmap_mode = c.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
        .address_mode_u = c.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
        .address_mode_v = c.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
        .address_mode_w = c.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
        .mip_lod_bias = 0,
        .max_anisotropy = 1,
        .compare_op = c.SDL_GPU_COMPAREOP_LESS_OR_EQUAL,
        .min_lod = 0,
        .max_lod = 1000,
        .enable_anisotropy = false,
        .enable_compare = true,
        .padding1 = 0,
        .padding2 = 0,
        .props = 0,
    }) orelse error.SamplerCreate;
}

/// Render scene depth from the light's point of view into the shadow map.
fn renderShadowPass(cmd: *c.SDL_GPUCommandBuffer, light_vp: Matrix4, objects: []const engine.SceneNode) void {
    const shadow_map = g_shadow_map orelse return;
    const pipeline = g_shadow_pipeline orelse return;

    var depth_info = std.mem.zeroes(c.SDL_GPUDepthStencilTargetInfo);
    depth_info.texture = shadow_map;
    depth_info.load_op = c.SDL_GPU_LOADOP_CLEAR;
    depth_info.store_op = c.SDL_GPU_STOREOP_STORE;
    depth_info.clear_depth = 1.0;

    const pass = c.SDL_BeginGPURenderPass(cmd, null, 0, &depth_info) orelse return;
    defer c.SDL_EndGPURenderPass(pass);
    c.SDL_BindGPUGraphicsPipeline(pass, pipeline);
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(SHADOW_DIM),
        .h = @floatFromInt(SHADOW_DIM),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });

    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            if (!comp.mesh_renderer.cast_shadows) continue;
            const guid_str = comp.mesh_renderer.mesh.slice();
            if (guid_str.len == 0) continue;
            const mp = EditorState.resolveAssetGuid(guid_str) orelse continue;
            const gm = findGpuMesh(mp) orelse continue;
            if (gm.idx_count == 0) continue;

            const t = &obj.transform;
            const mdl = Matrix4.translation(t.position.x, t.position.y, t.position.z)
                .multiply(Matrix4.rotationEuler(t.rotation.x, t.rotation.y, t.rotation.z))
                .multiply(Matrix4.scaling(t.scale.x, t.scale.y, t.scale.z));
            const lub = ShadowUB{ .light_mvp = light_vp.multiply(mdl).m };
            c.SDL_PushGPUVertexUniformData(cmd, 0, &lub, @sizeOf(ShadowUB));

            c.SDL_BindGPUVertexBuffers(pass, 0, &c.SDL_GPUBufferBinding{ .buffer = gm.vtx_buf, .offset = 0 }, 1);
            c.SDL_BindGPUIndexBuffer(pass, &c.SDL_GPUBufferBinding{ .buffer = gm.idx_buf, .offset = 0 }, c.SDL_GPU_INDEXELEMENTSIZE_32BIT);
            c.SDL_DrawGPUIndexedPrimitives(pass, gm.idx_count, 1, 0, 0, 0);
        }
    }
}

/// Resolve a mesh renderer's material GUID into scalar values and resolved
/// texture paths. The `.material` file is small, so it is parsed each frame,
/// keeping the viewport live while editing a material in the inspector.
fn resolveMaterial(mat_guid: []const u8) ResolvedMaterial {
    var out = ResolvedMaterial{};
    if (mat_guid.len == 0) return out;

    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();

    const mat = blk: {
        if (EditorState.resolveAssetGuid(mat_guid)) |path| {
            break :blk engine.Material.load(arena.allocator(), dvui.io, path) catch return out;
        }
        var builtin_buf: [64 * 1024]u8 = undefined;
        const bytes = engine.Material.builtinBytes(mat_guid, &builtin_buf) orelse return out;
        break :blk engine.Material.loadFromBytes(arena.allocator(), bytes) catch return out;
    };

    out.base_color = mat.vector("base_color", out.base_color);
    out.metallic = mat.scalar("metallic", out.metallic);
    out.roughness = mat.scalar("roughness", out.roughness);
    out.normal_scale = mat.scalar("normal_scale", out.normal_scale);
    out.occlusion_strength = mat.scalar("occlusion_strength", out.occlusion_strength);
    out.alpha_cutoff = mat.scalar("alpha_cutoff", out.alpha_cutoff);
    out.emissive_strength = mat.scalar("emissive_strength", out.emissive_strength);
    const em = mat.vector("emissive", .{ 0, 0, 0, 1 });
    out.emissive = .{ em[0], em[1], em[2] };

    const names = [_]struct { slot: MapSlot, key: []const u8 }{
        .{ .slot = .albedo, .key = "albedo_map" },
        .{ .slot = .mr, .key = "metallic_roughness_map" },
        .{ .slot = .normal, .key = "normal_map" },
        .{ .slot = .emissive, .key = "emissive_map" },
        .{ .slot = .occlusion, .key = "occlusion_map" },
    };
    for (names) |n| {
        const tex_guid = mat.texture(n.key);
        if (tex_guid.len == 0) continue;
        if (EditorState.resolveAssetGuid(tex_guid)) |tp| {
            out.maps[@intFromEnum(n.slot)].set(tp);
        }
    }
    return out;
}

fn findGpuMesh(path: []const u8) ?*GpuMesh {
    for (g_meshes[0..g_mesh_count]) |*gm|
        if (gm.matchesPath(path)) return gm;
    return null;
}

fn findGpuTexture(path: []const u8) ?*GpuTexture {
    for (g_textures[0..g_texture_count]) |*gt|
        if (gt.matchesPath(path)) return gt;
    return null;
}

const PickedTexture = struct { tex: *c.SDL_GPUTexture, found: bool };

/// Return the cached GPU texture for `path`, or `default_tex` when the path is
/// empty or the texture is not (yet) uploaded. `found` reports whether the real
/// texture was used, so the shader can branch on whether a map is present.
fn pickTexture(path: []const u8, default_tex: *c.SDL_GPUTexture) PickedTexture {
    if (path.len > 0) {
        if (findGpuTexture(path)) |gt| return .{ .tex = gt.texture, .found = true };
    }
    return .{ .tex = default_tex, .found = false };
}

fn present(found: bool) f32 {
    return if (found) 1.0 else 0.0;
}

fn uploadNewAssets(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, objects: []const engine.SceneNode) void {
    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .mesh_renderer) continue;
            const guid_str = comp.mesh_renderer.mesh.slice();
            if (guid_str.len == 0) continue;
            // Material textures must be uploaded here, before the render pass,
            // because copy passes cannot run inside a render pass.
            uploadMaterialTextures(cmd, dev, comp.mesh_renderer.material.slice());

            const mp = EditorState.resolveAssetGuid(guid_str) orelse continue;
            if (findGpuMesh(mp) != null or g_mesh_count >= MAX_MESHES) continue;
            uploadMesh(cmd, dev, mp) catch |err| {
                std.debug.print("[GpuRenderer] upload mesh '{s}': {any}\n", .{ mp, err });
                // Register as failed so we don't retry every frame.
                var gm = &g_meshes[g_mesh_count];
                g_mesh_count += 1;
                const l = @min(mp.len, 256);
                @memcpy(gm.path[0..l], mp[0..l]);
                gm.path_len = l;
                gm.idx_count = 0;
            };
        }
    }
}

/// Upload all texture maps referenced by a material (if not already cached).
/// Runs before the render pass. Upload failures are ignored — the draw falls
/// back to the white / flat-normal default for that slot.
fn uploadMaterialTextures(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, mat_guid: []const u8) void {
    if (mat_guid.len == 0) return;
    const rm = resolveMaterial(mat_guid);
    for (&rm.maps) |*m| {
        const p = m.slice();
        if (p.len == 0 or findGpuTexture(p) != null or g_texture_count >= MAX_TEXTURES) continue;
        if (uploadTexture(cmd, dev, p)) |_| {} else |_| {}
    }
}

fn uploadMesh(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, path: []const u8) !void {
    // `path` is the full path as stored in the AssetDatabase — do not prepend project_path.
    const full_path = path;
    const cpu = try engine.assets.loadMesh(std.heap.page_allocator, dvui.io, full_path);
    defer {
        var m = cpu;
        m.deinit();
    }

    if (cpu.vertices.len == 0 or cpu.indices.len == 0) return;

    const vtx_bytes: u32 = @intCast(cpu.vertices.len * @sizeOf(GpuVertex));
    const idx_bytes: u32 = @intCast(cpu.indices.len * @sizeOf(u32));

    const vtx_tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = vtx_bytes,
        .props = 0,
    }) orelse return error.VtxTransferCreate;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, vtx_tb);

    {
        const p: [*]GpuVertex = @ptrCast(@alignCast(
            c.SDL_MapGPUTransferBuffer(dev, vtx_tb, false) orelse return error.MapFailed,
        ));
        for (cpu.vertices, 0..) |v, i|
            p[i] = .{ .px = v.px, .py = v.py, .pz = v.pz, .nx = v.nx, .ny = v.ny, .nz = v.nz, .u = v.u, .v = v.v };
        c.SDL_UnmapGPUTransferBuffer(dev, vtx_tb);
    }

    const idx_tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = idx_bytes,
        .props = 0,
    }) orelse return error.IdxTransferCreate;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, idx_tb);

    {
        const p: [*]u32 = @ptrCast(@alignCast(
            c.SDL_MapGPUTransferBuffer(dev, idx_tb, false) orelse return error.MapFailed,
        ));
        @memcpy(p[0..cpu.indices.len], cpu.indices);
        c.SDL_UnmapGPUTransferBuffer(dev, idx_tb);
    }

    const vtx_buf = c.SDL_CreateGPUBuffer(dev, &c.SDL_GPUBufferCreateInfo{
        .usage = c.SDL_GPU_BUFFERUSAGE_VERTEX,
        .size = vtx_bytes,
        .props = 0,
    }) orelse return error.VtxBufCreate;
    errdefer c.SDL_ReleaseGPUBuffer(dev, vtx_buf);

    const idx_buf = c.SDL_CreateGPUBuffer(dev, &c.SDL_GPUBufferCreateInfo{
        .usage = c.SDL_GPU_BUFFERUSAGE_INDEX,
        .size = idx_bytes,
        .props = 0,
    }) orelse return error.IdxBufCreate;
    errdefer c.SDL_ReleaseGPUBuffer(dev, idx_buf);

    const cp = c.SDL_BeginGPUCopyPass(cmd) orelse return error.CopyPassFailed;
    c.SDL_UploadToGPUBuffer(cp, &c.SDL_GPUTransferBufferLocation{ .transfer_buffer = vtx_tb, .offset = 0 }, &c.SDL_GPUBufferRegion{ .buffer = vtx_buf, .offset = 0, .size = vtx_bytes }, false);
    c.SDL_UploadToGPUBuffer(cp, &c.SDL_GPUTransferBufferLocation{ .transfer_buffer = idx_tb, .offset = 0 }, &c.SDL_GPUBufferRegion{ .buffer = idx_buf, .offset = 0, .size = idx_bytes }, false);
    c.SDL_EndGPUCopyPass(cp);

    var gm = &g_meshes[g_mesh_count];
    g_mesh_count += 1;
    const l = @min(path.len, 256);
    @memcpy(gm.path[0..l], path[0..l]);
    gm.path_len = l;
    gm.vtx_buf = vtx_buf;
    gm.idx_buf = idx_buf;
    gm.idx_count = @intCast(cpu.indices.len);
}

/// Upload a texture from disk to the GPU and cache it.
pub fn uploadTexture(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, path: []const u8) !*c.SDL_GPUTexture {
    if (findGpuTexture(path)) |gt| return gt.texture;
    if (g_texture_count >= MAX_TEXTURES) return error.TextureCacheFull;

    var cpu = try engine.assets.loadTexture(std.heap.page_allocator, dvui.io, path);
    defer cpu.deinit();

    const tex_bytes: u32 = cpu.width * cpu.height * 4;

    const tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = tex_bytes,
        .props = 0,
    }) orelse return error.TransferCreate;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, tb);

    {
        const p: [*]u8 = @ptrCast(@alignCast(
            c.SDL_MapGPUTransferBuffer(dev, tb, false) orelse return error.MapFailed,
        ));
        @memcpy(p[0..tex_bytes], cpu.data);
        c.SDL_UnmapGPUTransferBuffer(dev, tb);
    }

    const gpu_tex = c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER,
        .width = cpu.width,
        .height = cpu.height,
        .layer_count_or_depth = 1,
        .num_levels = 1,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse return error.TextureCreate;
    errdefer c.SDL_ReleaseGPUTexture(dev, gpu_tex);

    const cp = c.SDL_BeginGPUCopyPass(cmd) orelse return error.CopyPassFailed;
    c.SDL_UploadToGPUTexture(cp, &c.SDL_GPUTextureTransferInfo{ .transfer_buffer = tb, .offset = 0, .pixels_per_row = cpu.width, .rows_per_layer = cpu.height }, &c.SDL_GPUTextureRegion{ .texture = gpu_tex, .mip_level = 0, .layer = 0, .x = 0, .y = 0, .z = 0, .w = cpu.width, .h = cpu.height, .d = 1 }, false);
    c.SDL_EndGPUCopyPass(cp);

    var gt = &g_textures[g_texture_count];
    g_texture_count += 1;
    const l = @min(path.len, 256);
    @memcpy(gt.path[0..l], path[0..l]);
    gt.path_len = l;
    gt.texture = gpu_tex;
    return gpu_tex;
}

fn createPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/scene.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/scene.frag.spv");

    const vert = c.SDL_CreateGPUShader(dev, &c.SDL_GPUShaderCreateInfo{
        .code_size = vert_spv.len,
        .code = vert_spv,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = c.SDL_GPU_SHADERSTAGE_VERTEX,
        .num_samplers = 0,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = 1,
        .props = 0,
    }) orelse return error.VertShader;
    defer c.SDL_ReleaseGPUShader(dev, vert);

    const frag = c.SDL_CreateGPUShader(dev, &c.SDL_GPUShaderCreateInfo{
        .code_size = frag_spv.len,
        .code = frag_spv,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = c.SDL_GPU_SHADERSTAGE_FRAGMENT,
        .num_samplers = 6,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = 1,
        .props = 0,
    }) orelse return error.FragShader;
    defer c.SDL_ReleaseGPUShader(dev, frag);

    const vtx_attrs = [_]c.SDL_GPUVertexAttribute{
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, .location = 0, .offset = 0 },
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, .location = 1, .offset = 12 },
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, .location = 2, .offset = 24 },
    };
    const vtx_bufs = [_]c.SDL_GPUVertexBufferDescription{.{
        .slot = 0,
        .pitch = @sizeOf(GpuVertex),
        .input_rate = c.SDL_GPU_VERTEXINPUTRATE_VERTEX,
        .instance_step_rate = 0,
    }};
    const color_desc = c.SDL_GPUColorTargetDescription{
        .format = c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
        .blend_state = std.mem.zeroes(c.SDL_GPUColorTargetBlendState),
    };

    var info = std.mem.zeroes(c.SDL_GPUGraphicsPipelineCreateInfo);
    info.vertex_shader = vert;
    info.fragment_shader = frag;
    info.primitive_type = c.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
    info.vertex_input_state = .{
        .num_vertex_buffers = 1,
        .vertex_buffer_descriptions = &vtx_bufs,
        .num_vertex_attributes = 3,
        .vertex_attributes = &vtx_attrs,
    };
    info.rasterizer_state = .{
        .fill_mode = c.SDL_GPU_FILLMODE_FILL,
        .cull_mode = c.SDL_GPU_CULLMODE_BACK,
        .front_face = c.SDL_GPU_FRONTFACE_COUNTER_CLOCKWISE,
        .depth_bias_constant_factor = 0,
        .depth_bias_clamp = 0,
        .depth_bias_slope_factor = 0,
        .enable_depth_bias = false,
        .enable_depth_clip = true,
        .padding1 = 0,
        .padding2 = 0,
    };
    info.depth_stencil_state = .{
        .compare_op = c.SDL_GPU_COMPAREOP_LESS,
        .back_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .front_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .compare_mask = 0xff,
        .write_mask = 0xff,
        .enable_depth_test = true,
        .enable_depth_write = true,
        .enable_stencil_test = false,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };
    info.target_info = .{
        .num_color_targets = 1,
        .color_target_descriptions = &color_desc,
        .depth_stencil_format = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM,
        .has_depth_stencil_target = true,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };

    return c.SDL_CreateGPUGraphicsPipeline(dev, &info) orelse error.Pipeline;
}

/// Depth-only pipeline for rendering the directional-light shadow map.
fn createShadowPipeline(dev: *c.SDL_GPUDevice) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/shadow.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/shadow.frag.spv");

    const vert = c.SDL_CreateGPUShader(dev, &c.SDL_GPUShaderCreateInfo{
        .code_size = vert_spv.len,
        .code = vert_spv,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = c.SDL_GPU_SHADERSTAGE_VERTEX,
        .num_samplers = 0,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = 1,
        .props = 0,
    }) orelse return error.VertShader;
    defer c.SDL_ReleaseGPUShader(dev, vert);

    const frag = c.SDL_CreateGPUShader(dev, &c.SDL_GPUShaderCreateInfo{
        .code_size = frag_spv.len,
        .code = frag_spv,
        .entrypoint = "main",
        .format = c.SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = c.SDL_GPU_SHADERSTAGE_FRAGMENT,
        .num_samplers = 0,
        .num_storage_textures = 0,
        .num_storage_buffers = 0,
        .num_uniform_buffers = 0,
        .props = 0,
    }) orelse return error.FragShader;
    defer c.SDL_ReleaseGPUShader(dev, frag);

    const vtx_attrs = [_]c.SDL_GPUVertexAttribute{
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, .location = 0, .offset = 0 },
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, .location = 1, .offset = 12 },
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, .location = 2, .offset = 24 },
    };
    const vtx_bufs = [_]c.SDL_GPUVertexBufferDescription{.{
        .slot = 0,
        .pitch = @sizeOf(GpuVertex),
        .input_rate = c.SDL_GPU_VERTEXINPUTRATE_VERTEX,
        .instance_step_rate = 0,
    }};

    var info = std.mem.zeroes(c.SDL_GPUGraphicsPipelineCreateInfo);
    info.vertex_shader = vert;
    info.fragment_shader = frag;
    info.primitive_type = c.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
    info.vertex_input_state = .{
        .num_vertex_buffers = 1,
        .vertex_buffer_descriptions = &vtx_bufs,
        .num_vertex_attributes = 3,
        .vertex_attributes = &vtx_attrs,
    };
    // Cull nothing and apply depth bias to combat shadow acne / peter-panning.
    info.rasterizer_state = .{
        .fill_mode = c.SDL_GPU_FILLMODE_FILL,
        .cull_mode = c.SDL_GPU_CULLMODE_NONE,
        .front_face = c.SDL_GPU_FRONTFACE_COUNTER_CLOCKWISE,
        .depth_bias_constant_factor = 1.25,
        .depth_bias_clamp = 0,
        .depth_bias_slope_factor = 1.75,
        .enable_depth_bias = true,
        .enable_depth_clip = true,
        .padding1 = 0,
        .padding2 = 0,
    };
    info.depth_stencil_state = .{
        .compare_op = c.SDL_GPU_COMPAREOP_LESS,
        .back_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .front_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .compare_mask = 0xff,
        .write_mask = 0xff,
        .enable_depth_test = true,
        .enable_depth_write = true,
        .enable_stencil_test = false,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };
    info.target_info = .{
        .num_color_targets = 0,
        .color_target_descriptions = null,
        .depth_stencil_format = SHADOW_FORMAT,
        .has_depth_stencil_target = true,
        .padding1 = 0,
        .padding2 = 0,
        .padding3 = 0,
    };

    return c.SDL_CreateGPUGraphicsPipeline(dev, &info) orelse error.Pipeline;
}
