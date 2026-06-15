//! Shared GPU state for the renderer (global singleton — one renderer per
//! process). Other render files read/write this; the public API lives in
//! `root.zig`.
const gpu = @import("gpu");
const types = @import("types.zig");

const c = gpu.c;

pub const SHADOW_FORMAT = c.SDL_GPU_TEXTUREFORMAT_D16_UNORM;

pub var device: ?*c.SDL_GPUDevice = null;
pub var pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var sampler: ?*c.SDL_GPUSampler = null;

pub var shadow_pipeline: ?*c.SDL_GPUGraphicsPipeline = null;
pub var shadow_map: ?*c.SDL_GPUTexture = null;
pub var shadow_sampler: ?*c.SDL_GPUSampler = null;

pub var white_tex: ?*c.SDL_GPUTexture = null;
/// Default tangent-space "flat" normal (points straight out): rgb (128,128,255).
pub var flat_normal_tex: ?*c.SDL_GPUTexture = null;

pub var depth_tex: ?*c.SDL_GPUTexture = null;
pub var target_w: u32 = 0;
pub var target_h: u32 = 0;

// Asset sources (GUID → bytes).
pub var mesh_src: ?types.SourceFn = null;
pub var texture_src: ?types.SourceFn = null;
pub var material_src: ?types.SourceFn = null;

// GPU resource caches keyed by asset GUID (≤36 chars).
pub const KEY_CAP = 64;

pub const MAX_MESHES = 64;
pub const GpuMesh = struct {
    key: [KEY_CAP]u8 = undefined,
    key_len: usize = 0,
    vtx_buf: *c.SDL_GPUBuffer = undefined,
    idx_buf: *c.SDL_GPUBuffer = undefined,
    idx_count: u32 = 0,

    pub fn matchesKey(self: *const @This(), k: []const u8) bool {
        return std.mem.eql(u8, self.key[0..self.key_len], k);
    }
};
pub var meshes: [MAX_MESHES]GpuMesh = undefined;
pub var mesh_count: usize = 0;

pub const MAX_TEXTURES = 64;
pub const GpuTexture = struct {
    key: [KEY_CAP]u8 = undefined,
    key_len: usize = 0,
    texture: *c.SDL_GPUTexture = undefined,

    pub fn matchesKey(self: *const @This(), k: []const u8) bool {
        return std.mem.eql(u8, self.key[0..self.key_len], k);
    }
};
pub var textures: [MAX_TEXTURES]GpuTexture = undefined;
pub var texture_count: usize = 0;

const std = @import("std");
