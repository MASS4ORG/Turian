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
            if (comp.* != .mesh_renderer) continue;
            const guid = comp.mesh_renderer.mesh.slice();
            if (guid.len == 0) continue;
            uploadMaterialTextures(cmd, dev, comp.mesh_renderer.material.slice());

            if (findGpuMesh(guid) != null or state.mesh_count >= state.MAX_MESHES) continue;
            uploadMesh(cmd, dev, guid) catch {
                // Register as failed so we don't retry every frame.
                var gm = &state.meshes[state.mesh_count];
                state.mesh_count += 1;
                gm.key_len = setKey(&gm.key, guid);
                gm.idx_count = 0;
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
        if (guid.len == 0 or findGpuTexture(guid) != null or state.texture_count >= state.MAX_TEXTURES) continue;
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

    var gm = &state.meshes[state.mesh_count];
    state.mesh_count += 1;
    gm.key_len = setKey(&gm.key, guid);
    gm.vtx_buf = vtx_buf;
    gm.idx_buf = idx_buf;
    gm.idx_count = @intCast(cpu.indices.len);
}

/// Upload a texture (RGBA8 or block-compressed, with mips) and cache it by GUID.
pub fn uploadTexture(cmd: *c.SDL_GPUCommandBuffer, dev: *c.SDL_GPUDevice, guid: []const u8) !*c.SDL_GPUTexture {
    if (findGpuTexture(guid)) |gt| return gt.texture;
    if (state.texture_count >= state.MAX_TEXTURES) return error.TextureCacheFull;

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
    return gpu_tex;
}
