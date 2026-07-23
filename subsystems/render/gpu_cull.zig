//! GPU-driven frustum culling: a compute pass that writes per-submesh indirect
//! draw commands, then the render pass issues one `DrawIndirect` per material group.
const gpu = @import("gpu");
const engine = @import("engine");
const types = @import("types.zig");
const state = @import("state.zig");
const culling = @import("culling.zig");

const c = gpu.c;
const Matrix4 = engine.Matrix4;

/// Dispatches the cull compute pass for `gm`, writing `gm.indirect_buf` in place.
pub fn dispatchCull(cmd: *c.SDL_GPUCommandBuffer, gm: *const state.GpuMesh, model: Matrix4, frustum: culling.Frustum) void {
    const cull_pl = state.cull_pipeline orelse return;
    const bounds_buf = gm.bounds_buf orelse return;
    const indirect_buf = gm.indirect_buf orelse return;
    if (gm.submeshes.len == 0) return;

    var planes: [6][4]f32 = undefined;
    for (frustum.planes, 0..) |p, i| planes[i] = .{ p.a, p.b, p.c, p.d };
    const ub = types.CullUB{
        .model = model.m,
        .planes = planes,
        .submesh_count = .{ @intCast(gm.submeshes.len), 0, 0, 0 },
    };

    const cpass = c.SDL_BeginGPUComputePass(
        cmd,
        null,
        0,
        &[_]c.SDL_GPUStorageBufferReadWriteBinding{.{ .buffer = indirect_buf, .cycle = false, .padding1 = 0, .padding2 = 0, .padding3 = 0 }},
        1,
    ) orelse return;
    c.SDL_BindGPUComputePipeline(cpass, cull_pl);
    c.SDL_BindGPUComputeStorageBuffers(cpass, 0, &[_]?*c.SDL_GPUBuffer{bounds_buf}, 1);
    c.SDL_PushGPUComputeUniformData(cmd, 0, &ub, @sizeOf(types.CullUB));
    const groups: u32 = @intCast((gm.submeshes.len + 63) / 64);
    c.SDL_DispatchGPUCompute(cpass, groups, 1, 1);
    c.SDL_EndGPUComputePass(cpass);
}
