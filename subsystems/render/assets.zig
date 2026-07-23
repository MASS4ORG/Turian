//! GUID-keyed GPU resource upload (meshes, textures) and material resolution.
//! Geometry/texture/material bytes are pulled through the source callbacks in
//! `state`, so this never touches the filesystem.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");

const c = gpu.c;
const page = std.heap.page_allocator;
const log = std.log.scoped(.render_assets);

pub fn findGpuMesh(guid: []const u8) ?*state.GpuMesh {
    for (state.meshes[0..state.mesh_count]) |*gm|
        if (gm.matchesKey(guid)) return gm;
    return null;
}

pub fn findGpuTexture(guid: []const u8) ?*state.GpuTexture {
    for (state.textures[0..state.texture_count]) |*gt|
        if (gt.matchesKey(guid)) return gt;
    return null;
}

pub const PickedTexture = struct { tex: *c.SDL_GPUTexture, found: bool };

/// Cached GPU texture for `guid`, or `default_tex` when unbound/not uploaded.
pub fn pickTexture(guid: []const u8, default_tex: *c.SDL_GPUTexture) PickedTexture {
    if (guid.len > 0) {
        if (findGpuTexture(guid)) |gt| return .{ .tex = gt.texture, .found = true };
    }
    return .{ .tex = default_tex, .found = false };
}

pub fn present(found: bool) f32 {
    return if (found) 1.0 else 0.0;
}

/// Map an engine texture format to its SDL3 GPU equivalent.
pub fn sdlTextureFormat(fmt: engine.assets.TextureFormat) c.SDL_GPUTextureFormat {
    return switch (fmt) {
        .rgba8_unorm => c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
        .rgba8_srgb => c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM_SRGB,
        .bc1_rgb_unorm => c.SDL_GPU_TEXTUREFORMAT_BC1_RGBA_UNORM,
        .bc1_rgb_srgb => c.SDL_GPU_TEXTUREFORMAT_BC1_RGBA_UNORM_SRGB,
        .bc3_unorm => c.SDL_GPU_TEXTUREFORMAT_BC3_RGBA_UNORM,
        .bc3_srgb => c.SDL_GPU_TEXTUREFORMAT_BC3_RGBA_UNORM_SRGB,
        .bc4_unorm => c.SDL_GPU_TEXTUREFORMAT_BC4_R_UNORM,
        .bc5_unorm => c.SDL_GPU_TEXTUREFORMAT_BC5_RG_UNORM,
        .bc7_unorm => c.SDL_GPU_TEXTUREFORMAT_BC7_RGBA_UNORM,
        .bc7_srgb => c.SDL_GPU_TEXTUREFORMAT_BC7_RGBA_UNORM_SRGB,
    };
}

/// Resolve a mesh renderer's material GUID into scalar values and the GUIDs of
/// its texture maps. The `.material` bytes come from the material source; small,
/// so re-parsed each frame to keep the viewport live while editing.
pub fn resolveMaterial(mat_guid: []const u8) types.ResolvedMaterial {
    var out = types.ResolvedMaterial{};
    if (mat_guid.len == 0) return out;

    var arena = std.heap.ArenaAllocator.init(page);
    defer arena.deinit();

    const is_override = mat_guid.len == state.material_override_key_len and
        std.mem.eql(u8, mat_guid, state.material_override_key[0..state.material_override_key_len]);
    const mat = if (is_override)
        engine.Material.loadFromBytes(arena.allocator(), state.material_override_bytes) catch return out
    else blk: {
        const src = state.material_src orelse return out;
        const b = src(mat_guid) orelse return out;
        defer if (b.owned) page.free(b.data);
        break :blk engine.Material.loadFromBytes(arena.allocator(), b.data) catch return out;
    };

    out.base_color = mat.vector("base_color", out.base_color);
    out.metallic = mat.scalar("metallic", out.metallic);
    out.roughness = mat.scalar("roughness", out.roughness);
    out.normal_scale = mat.scalar("normal_scale", out.normal_scale);
    out.occlusion_strength = mat.scalar("occlusion_strength", out.occlusion_strength);
    out.alpha_cutoff = mat.scalar("alpha_cutoff", out.alpha_cutoff);
    out.emissive_strength = mat.scalar("emissive_strength", out.emissive_strength);
    out.render = mat.render;
    const em = mat.vector("emissive", .{ 0, 0, 0, 1 });
    out.emissive = .{ em[0], em[1], em[2] };

    const names = [_]struct { slot: types.MapSlot, key: []const u8 }{
        .{ .slot = .albedo, .key = "albedo_map" },
        .{ .slot = .mr, .key = "metallic_roughness_map" },
        .{ .slot = .normal, .key = "normal_map" },
        .{ .slot = .emissive, .key = "emissive_map" },
        .{ .slot = .occlusion, .key = "occlusion_map" },
    };
    for (names) |n| {
        const tex_guid = mat.texture(n.key);
        if (tex_guid.len > 0) out.maps[@intFromEnum(n.slot)].set(tex_guid);
    }
    return out;
}

fn setKey(dst: []u8, s: []const u8) usize {
    const l = @min(s.len, dst.len);
    @memcpy(dst[0..l], s[0..l]);
    return l;
}

/// Upload every mesh + material texture referenced by `objects` that isn't
/// cached yet. Must run before the render pass (copy passes can't be nested in
/// a render pass).
pub fn uploadNewAssets(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, objects: []const engine.SceneNode) void {
    for (objects) |*obj| {
        if (!obj.active) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            switch (comp.*) {
                .mesh_renderer => {},
                .environment => {
                    const guid = comp.environment.env_map.slice();
                    if (guid.len > 0 and findGpuTexture(guid) == null) {
                        state.ensureTextureCapacity();
                        if (state.texture_count < state.textures.len)
                            uploadEnvironment(cmd, dev, guid) catch |err|
                                log.warn("environment upload failed: {any}", .{err});
                    }
                    continue;
                },
                else => continue,
            }
            const guid = comp.mesh_renderer.mesh.slice();
            if (guid.len == 0) continue;
            const mr = &comp.mesh_renderer;
            const mat_n = @min(mr.material_count, engine.MeshRendererComponent.MAX_MATERIALS);
            for (mr.materials[0..mat_n]) |*mat_ref| uploadMaterialTextures(cmd, dev, mat_ref.slice());

            if (findGpuMesh(guid) != null) continue;
            state.ensureMeshCapacity();
            if (state.mesh_count >= state.meshes.len) continue; // OOM growing the cache
            uploadMesh(cmd, dev, guid) catch {
                // Register as failed so we don't retry every frame.
                var gm = &state.meshes[state.mesh_count];
                state.mesh_count += 1;
                gm.key_len = setKey(&gm.key, guid);
                gm.idx_count = 0;
                gm.submeshes = &.{};
            };
        }
    }
}

/// Upload all texture maps referenced by a material (if not already cached).
fn uploadMaterialTextures(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, mat_guid: []const u8) void {
    if (mat_guid.len == 0) return;
    const rm = resolveMaterial(mat_guid);
    for (&rm.maps) |*m| {
        const guid = m.slice();
        if (guid.len == 0 or findGpuTexture(guid) != null) continue;
        state.ensureTextureCapacity();
        if (state.texture_count >= state.textures.len) continue; // OOM growing the cache
        if (uploadTexture(cmd, dev, guid)) |_| {} else |_| {}
    }
}

fn uploadMesh(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, guid: []const u8) !void {
    const src = state.mesh_src orelse return error.NoMeshSource;
    const b = src(guid) orelse return error.MeshNotFound;
    defer if (b.owned) page.free(b.data);

    var cpu = try engine.assets.loadMeshFromMemory(page, b.data, "");
    defer cpu.deinit();
    if (cpu.vertices.len == 0 or cpu.indices.len == 0) return;

    const vtx_bytes: u32 = @intCast(cpu.vertices.len * @sizeOf(types.GpuVertex));
    const idx_bytes: u32 = @intCast(cpu.indices.len * @sizeOf(u32));

    // Build the (sorted) submesh + material-group tables first — pure CPU
    // work, no GPU calls — so the per-submesh bounds buffer below can be
    // filled and uploaded in the same copy pass as the vertex/index buffers.
    //
    // One GPU submesh per cooked submesh (no ceiling); meshes with no submesh
    // table draw as a single implicit range bound to material slot 0.
    const sm_count = @max(cpu.submeshes.len, 1);
    const submeshes = try page.alloc(state.GpuSubmesh, sm_count);
    errdefer page.free(submeshes);
    if (cpu.submeshes.len == 0) {
        submeshes[0] = .{
            .index_offset = 0,
            .index_count = @intCast(cpu.indices.len),
            .material_slot = 0,
            .bounds_min = cpu.min,
            .bounds_max = cpu.max,
        };
    } else {
        for (cpu.submeshes, cpu.submesh_bounds, 0..) |sm, sb, i|
            submeshes[i] = .{
                .index_offset = sm.index_offset,
                .index_count = sm.index_count,
                .material_slot = sm.material_slot,
                .bounds_min = sb.min,
                .bounds_max = sb.max,
            };
    }

    // Sort by material slot so same-material submeshes end up contiguous —
    // lets the renderer issue one indirect multi-draw call per material
    // instead of one draw per submesh. Draw order within a mesh doesn't
    // matter (each submesh is an independent opaque range), so this is safe.
    std.sort.pdq(state.GpuSubmesh, submeshes, {}, struct {
        fn lessThan(_: void, x: state.GpuSubmesh, y: state.GpuSubmesh) bool {
            return x.material_slot < y.material_slot;
        }
    }.lessThan);

    var group_count: usize = 1;
    for (submeshes[1..], 1..) |sm, i|
        if (sm.material_slot != submeshes[i - 1].material_slot) {
            group_count += 1;
        };
    const groups = try page.alloc(state.MaterialGroup, group_count);
    errdefer page.free(groups);
    {
        var gi: usize = 0;
        var i: usize = 0;
        while (i < submeshes.len) {
            const slot = submeshes[i].material_slot;
            var j = i + 1;
            while (j < submeshes.len and submeshes[j].material_slot == slot) j += 1;
            groups[gi] = .{ .material_slot = slot, .start = @intCast(i), .count = @intCast(j - i) };
            gi += 1;
            i = j;
        }
    }

    const bounds_data = try page.alloc(types.SubmeshBoundsGpu, submeshes.len);
    defer page.free(bounds_data);
    for (submeshes, bounds_data) |sm, *bd|
        bd.* = .{
            .min = .{ sm.bounds_min[0], sm.bounds_min[1], sm.bounds_min[2], 0 },
            .max = .{ sm.bounds_max[0], sm.bounds_max[1], sm.bounds_max[2], 0 },
            .range = .{ sm.index_offset, sm.index_count, 0, 0 },
        };
    const bounds_bytes: u32 = @intCast(bounds_data.len * @sizeOf(types.SubmeshBoundsGpu));
    const indirect_bytes: u32 = @intCast(submeshes.len * @sizeOf(c.SDL_GPUIndexedIndirectDrawCommand));

    const vtx_tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = vtx_bytes,
        .props = 0,
    }) orelse return error.VtxTransferCreate;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, vtx_tb);

    {
        const p: [*]types.GpuVertex = @ptrCast(@alignCast(
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

    const bounds_tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = bounds_bytes,
        .props = 0,
    }) orelse return error.BoundsTransferCreate;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, bounds_tb);

    {
        const p: [*]types.SubmeshBoundsGpu = @ptrCast(@alignCast(
            c.SDL_MapGPUTransferBuffer(dev, bounds_tb, false) orelse return error.MapFailed,
        ));
        @memcpy(p[0..bounds_data.len], bounds_data);
        c.SDL_UnmapGPUTransferBuffer(dev, bounds_tb);
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

    const bounds_buf = c.SDL_CreateGPUBuffer(dev, &c.SDL_GPUBufferCreateInfo{
        .usage = c.SDL_GPU_BUFFERUSAGE_COMPUTE_STORAGE_READ,
        .size = bounds_bytes,
        .props = 0,
    }) orelse return error.BoundsBufCreate;
    errdefer c.SDL_ReleaseGPUBuffer(dev, bounds_buf);

    // No initial data: the cull compute pass fully overwrites every entry
    // before the first indirect draw reads it, every frame.
    const indirect_buf = c.SDL_CreateGPUBuffer(dev, &c.SDL_GPUBufferCreateInfo{
        .usage = c.SDL_GPU_BUFFERUSAGE_INDIRECT | c.SDL_GPU_BUFFERUSAGE_COMPUTE_STORAGE_WRITE,
        .size = indirect_bytes,
        .props = 0,
    }) orelse return error.IndirectBufCreate;
    errdefer c.SDL_ReleaseGPUBuffer(dev, indirect_buf);

    const cp = c.SDL_BeginGPUCopyPass(cmd) orelse return error.CopyPassFailed;
    c.SDL_UploadToGPUBuffer(cp, &c.SDL_GPUTransferBufferLocation{ .transfer_buffer = vtx_tb, .offset = 0 }, &c.SDL_GPUBufferRegion{ .buffer = vtx_buf, .offset = 0, .size = vtx_bytes }, false);
    c.SDL_UploadToGPUBuffer(cp, &c.SDL_GPUTransferBufferLocation{ .transfer_buffer = idx_tb, .offset = 0 }, &c.SDL_GPUBufferRegion{ .buffer = idx_buf, .offset = 0, .size = idx_bytes }, false);
    c.SDL_UploadToGPUBuffer(cp, &c.SDL_GPUTransferBufferLocation{ .transfer_buffer = bounds_tb, .offset = 0 }, &c.SDL_GPUBufferRegion{ .buffer = bounds_buf, .offset = 0, .size = bounds_bytes }, false);
    c.SDL_EndGPUCopyPass(cp);

    state.ensureMeshCapacity();
    if (state.mesh_count >= state.meshes.len) return error.MeshCacheFull;
    var gm = &state.meshes[state.mesh_count];
    state.mesh_count += 1;
    gm.key_len = setKey(&gm.key, guid);
    gm.vtx_buf = vtx_buf;
    gm.idx_buf = idx_buf;
    gm.idx_count = @intCast(cpu.indices.len);
    gm.submeshes = submeshes;
    gm.material_groups = groups;
    gm.bounds_min = cpu.min;
    gm.bounds_max = cpu.max;
    gm.bounds_buf = bounds_buf;
    gm.indirect_buf = indirect_buf;
}

/// Upload a texture (RGBA8 or block-compressed, with mips) and cache it by GUID.
pub fn uploadTexture(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, guid: []const u8) !*c.SDL_GPUTexture {
    if (findGpuTexture(guid)) |gt| return gt.texture;
    state.ensureTextureCapacity();
    if (state.texture_count >= state.textures.len) return error.TextureCacheFull;

    const src = state.texture_src orelse return error.NoTextureSource;
    const b = src(guid) orelse return error.TextureNotFound;
    defer if (b.owned) page.free(b.data);

    var cpu = try engine.assets.loadTextureFromMemory(page, b.data);
    defer cpu.deinit();

    const total_bytes: u32 = @intCast(cpu.data.len);
    const tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = total_bytes,
        .props = 0,
    }) orelse return error.TransferCreate;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, tb);

    {
        const p: [*]u8 = @ptrCast(@alignCast(
            c.SDL_MapGPUTransferBuffer(dev, tb, false) orelse return error.MapFailed,
        ));
        @memcpy(p[0..total_bytes], cpu.data);
        c.SDL_UnmapGPUTransferBuffer(dev, tb);
    }

    const compressed = cpu.isCompressed();
    const block: u32 = if (compressed) 4 else 1;
    const num_levels: u32 = @intCast(@max(cpu.mips.len, 1));

    const gpu_tex = c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = sdlTextureFormat(cpu.format),
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER,
        .width = cpu.width,
        .height = cpu.height,
        .layer_count_or_depth = 1,
        .num_levels = num_levels,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse return error.TextureCreate;
    errdefer c.SDL_ReleaseGPUTexture(dev, gpu_tex);

    const cp = c.SDL_BeginGPUCopyPass(cmd) orelse return error.CopyPassFailed;
    if (cpu.mips.len == 0) {
        c.SDL_UploadToGPUTexture(cp, &c.SDL_GPUTextureTransferInfo{ .transfer_buffer = tb, .offset = 0, .pixels_per_row = cpu.width, .rows_per_layer = cpu.height }, &c.SDL_GPUTextureRegion{ .texture = gpu_tex, .mip_level = 0, .layer = 0, .x = 0, .y = 0, .z = 0, .w = cpu.width, .h = cpu.height, .d = 1 }, false);
    } else {
        for (cpu.mips, 0..) |m, i| {
            const ppr = if (compressed) ((m.width + block - 1) / block) * block else m.width;
            const rpl = if (compressed) ((m.height + block - 1) / block) * block else m.height;
            c.SDL_UploadToGPUTexture(cp, &c.SDL_GPUTextureTransferInfo{ .transfer_buffer = tb, .offset = @intCast(m.offset), .pixels_per_row = ppr, .rows_per_layer = rpl }, &c.SDL_GPUTextureRegion{ .texture = gpu_tex, .mip_level = @intCast(i), .layer = 0, .x = 0, .y = 0, .z = 0, .w = m.width, .h = m.height, .d = 1 }, false);
        }
    }
    c.SDL_EndGPUCopyPass(cp);

    var gt = &state.textures[state.texture_count];
    state.texture_count += 1;
    gt.key_len = setKey(&gt.key, guid);
    gt.texture = gpu_tex;
    gt.env = null;
    return gpu_tex;
}

/// Full mip chain length for a 2D texture of size `w`×`h` (down to a 1×1 top level).
fn mipCountFor(w: u32, h: u32) u32 {
    return std.math.log2_int(u32, @max(@max(w, h), 1)) + 1;
}

/// Order-2 (9-coefficient) spherical-harmonics projection of an equirect
/// environment's radiance, for diffuse irradiance IBL (Ramamoorthi & Hanrahan).
/// Sampled on a coarse lat-long grid — order-2 SH is a very low-frequency
/// approximation, so a few thousand samples already saturate its accuracy;
/// walking every texel of a multi-megapixel HDRI would cost far more for no
/// visible gain.
const SH_SAMPLES_X = 128;
const SH_SAMPLES_Y = 64;

fn computeIrradianceSh(img: engine.assets.HdrLoader.HdrImage) [9][3]f32 {
    var sh: [9][3]f32 = @splat(@splat(0));
    const nx = @min(SH_SAMPLES_X, img.width);
    const ny = @min(SH_SAMPLES_Y, img.height);
    const nx_f: f32 = @floatFromInt(nx);
    const ny_f: f32 = @floatFromInt(ny);
    const dtheta = std.math.pi / ny_f;
    const dphi = 2.0 * std.math.pi / nx_f;

    for (0..ny) |sy| {
        const v = (@as(f32, @floatFromInt(sy)) + 0.5) / ny_f;
        const theta = v * std.math.pi;
        const sin_theta = @sin(theta);
        const cos_theta = @cos(theta);
        const weight = sin_theta * dtheta * dphi;
        const py: usize = @min(img.height - 1, (sy * img.height) / ny);

        for (0..nx) |sx| {
            const u = (@as(f32, @floatFromInt(sx)) + 0.5) / nx_f;
            const phi = (u - 0.5) * 2.0 * std.math.pi;
            const dx = sin_theta * @sin(phi);
            const dy = cos_theta;
            const dz = -sin_theta * @cos(phi);

            const px: usize = @min(img.width - 1, (sx * img.width) / nx);
            const rgb = img.pixels[(py * img.width + px) * 3 ..][0..3];

            const y_basis = [9]f32{
                0.282095,
                0.488603 * dy,
                0.488603 * dz,
                0.488603 * dx,
                1.092548 * dx * dy,
                1.092548 * dy * dz,
                0.315392 * (3.0 * dz * dz - 1.0),
                1.092548 * dx * dz,
                0.546274 * (dx * dx - dy * dy),
            };
            for (0..9) |i| {
                sh[i][0] += rgb[0] * y_basis[i] * weight;
                sh[i][1] += rgb[1] * y_basis[i] * weight;
                sh[i][2] += rgb[2] * y_basis[i] * weight;
            }
        }
    }
    return sh;
}

/// Upload an equirectangular HDR environment map as a float texture with a
/// full mip chain (specular IBL picks a mip by roughness) and cache it by GUID
/// alongside its precomputed diffuse-irradiance SH coefficients. Accepts either
/// a cooked `HdrLoader` envelope (the built/shipped-game asset source) or a raw
/// `.hdr` container (Studio's editor viewport reads source bytes directly,
/// without going through the import cook step). No-op if already cached.
pub fn uploadEnvironment(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, guid: []const u8) !void {
    if (findGpuTexture(guid)) |_| return;
    state.ensureTextureCapacity();
    if (state.texture_count >= state.textures.len) return error.TextureCacheFull;

    const src = state.texture_src orelse return error.NoTextureSource;
    const b = src(guid) orelse return error.TextureNotFound;
    defer if (b.owned) page.free(b.data);

    var img = if (engine.assets.HdrLoader.isEnvelope(b.data))
        try engine.assets.HdrLoader.decodeEnvelopeToImage(page, b.data)
    else
        try engine.assets.HdrLoader.decode(page, b.data);
    defer img.deinit();
    const w = img.width;
    const h = img.height;

    const sh = computeIrradianceSh(img);

    const pixel_count = @as(usize, w) * h;
    const half_pixels = try page.alloc(f16, pixel_count * 4);
    defer page.free(half_pixels);
    for (0..pixel_count) |i| {
        half_pixels[i * 4 + 0] = @floatCast(img.pixels[i * 3 + 0]);
        half_pixels[i * 4 + 1] = @floatCast(img.pixels[i * 3 + 1]);
        half_pixels[i * 4 + 2] = @floatCast(img.pixels[i * 3 + 2]);
        half_pixels[i * 4 + 3] = 1.0;
    }

    const total_bytes: u32 = @intCast(half_pixels.len * @sizeOf(f16));
    const tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = total_bytes,
        .props = 0,
    }) orelse return error.TransferCreate;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, tb);

    {
        const p: [*]f16 = @ptrCast(@alignCast(
            c.SDL_MapGPUTransferBuffer(dev, tb, false) orelse return error.MapFailed,
        ));
        @memcpy(p[0..half_pixels.len], half_pixels);
        c.SDL_UnmapGPUTransferBuffer(dev, tb);
    }

    const num_levels = mipCountFor(w, h);
    const gpu_tex = c.SDL_CreateGPUTexture(dev, &c.SDL_GPUTextureCreateInfo{
        .type = c.SDL_GPU_TEXTURETYPE_2D,
        .format = c.SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT,
        .usage = c.SDL_GPU_TEXTUREUSAGE_SAMPLER | c.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET,
        .width = w,
        .height = h,
        .layer_count_or_depth = 1,
        .num_levels = num_levels,
        .sample_count = c.SDL_GPU_SAMPLECOUNT_1,
        .props = 0,
    }) orelse return error.TextureCreate;
    errdefer c.SDL_ReleaseGPUTexture(dev, gpu_tex);

    const cp = c.SDL_BeginGPUCopyPass(cmd) orelse return error.CopyPassFailed;
    c.SDL_UploadToGPUTexture(cp, &c.SDL_GPUTextureTransferInfo{ .transfer_buffer = tb, .offset = 0, .pixels_per_row = w, .rows_per_layer = h }, &c.SDL_GPUTextureRegion{ .texture = gpu_tex, .mip_level = 0, .layer = 0, .x = 0, .y = 0, .z = 0, .w = w, .h = h, .d = 1 }, false);
    c.SDL_EndGPUCopyPass(cp);

    if (num_levels > 1) c.SDL_GenerateMipmapsForGPUTexture(cmd, gpu_tex);

    var gt = &state.textures[state.texture_count];
    state.texture_count += 1;
    gt.key_len = setKey(&gt.key, guid);
    gt.texture = gpu_tex;
    gt.env = .{ .mip_count = num_levels, .sh = sh };
}

// ── Tests ─────────────────────────────────────────────────────────────────────

test "mipCountFor spans full chains down to 1x1" {
    try std.testing.expectEqual(@as(u32, 1), mipCountFor(1, 1));
    try std.testing.expectEqual(@as(u32, 9), mipCountFor(256, 256));
    try std.testing.expectEqual(@as(u32, 13), mipCountFor(4096, 2048));
}

test "computeIrradianceSh reduces to a DC term for a uniform environment" {
    // A constant-radiance environment has zero higher-order SH components —
    // all directional information cancels out — so only sh[0] (the DC/average
    // term) should be non-zero.
    const w: u32 = 64;
    const h: u32 = 32;
    var pixels: [w * h * 3]f32 = undefined;
    for (0..w * h) |i| {
        // Constant linear color (arbitrary value picked to match a 128/128/128/128 RGBE quad).
        pixels[i * 3 + 0] = 0.5;
        pixels[i * 3 + 1] = 0.5;
        pixels[i * 3 + 2] = 0.5;
    }
    const img = engine.assets.HdrLoader.HdrImage{ .pixels = &pixels, .width = w, .height = h, .allocator = std.testing.allocator };

    const sh = computeIrradianceSh(img);

    // sh[0] integrates color * Y0 over the full sphere (solid angle 4*pi).
    const expect_dc = img.pixels[0] * 0.282095 * 4.0 * std.math.pi;
    try std.testing.expectApproxEqRel(expect_dc, sh[0][0], 0.01);

    for (1..9) |i| {
        try std.testing.expectApproxEqAbs(@as(f32, 0), sh[i][0], expect_dc * 0.01);
        try std.testing.expectApproxEqAbs(@as(f32, 0), sh[i][1], expect_dc * 0.01);
        try std.testing.expectApproxEqAbs(@as(f32, 0), sh[i][2], expect_dc * 0.01);
    }
}

test {
    std.testing.refAllDecls(@This());
}
