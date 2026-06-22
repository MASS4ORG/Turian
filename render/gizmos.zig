//! Gizmo line rendering (issue #3): draws the editor's debug/manipulation
//! overlay — colored line lists recorded into an `engine.Gizmos` buffer. The
//! geometry is UI-toolkit independent; the editor populates the buffer and the
//! studio viewport calls `renderGizmos` after `renderScene`.
const std = @import("std");
const gpu = @import("gpu");
const engine = @import("engine");
const state = @import("state.zig");

const c = gpu.c;

/// One recorded gizmo line vertex — identical layout to `engine.Gizmos.Vertex`
/// (position, RGBA color, screen-space thickness). The editor records these as
/// line-list pairs; `renderGizmos` expands each pair into a quad before upload.
pub const GizmoVertex = engine.Gizmos.Vertex;

/// Expanded per-corner vertex actually uploaded to the GPU. Each gizmo segment
/// becomes two triangles (six of these). The vertex shader projects both
/// endpoints, then offsets this corner perpendicular to the segment by the
/// thickness (in pixels) to produce a constant-width, camera-facing quad.
const ExpandedVertex = extern struct {
    a: [3]f32, // segment endpoint A (world)
    b: [3]f32, // segment endpoint B (world)
    color: [4]f32,
    thickness: f32, // this corner's width in pixels
    side: f32, // -1 / +1 — which side of the segment
    end: f32, // 0 = sit at A, 1 = sit at B
};

/// Vertex-stage uniforms for the gizmo pipeline. `viewport` (pixels) lets the
/// shader convert the pixel thickness into a clip-space offset.
pub const GizmoUB = extern struct {
    view_proj: [16]f32,
    viewport: [2]f32,
    _pad: [2]f32 = .{ 0, 0 },
};

const STRIDE = @sizeOf(ExpandedVertex);
/// Six expanded corners (two triangles) per recorded line segment.
const CORNERS_PER_SEGMENT = 6;

/// Create both gizmo pipelines (init time). `depth_test` selects between the
/// occluded world pipeline and the always-on-top overlay pipeline. Neither
/// writes depth, so gizmos never disturb the scene depth buffer.
pub fn createGizmoPipeline(dev: *c.SDL_GPUDevice, depth_test: bool) !*c.SDL_GPUGraphicsPipeline {
    const vert_spv = @embedFile("shaders/compiled/gizmo.vert.spv");
    const frag_spv = @embedFile("shaders/compiled/gizmo.frag.spv");

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
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, .location = 0, .offset = @offsetOf(ExpandedVertex, "a") },
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, .location = 1, .offset = @offsetOf(ExpandedVertex, "b") },
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, .location = 2, .offset = @offsetOf(ExpandedVertex, "color") },
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT, .location = 3, .offset = @offsetOf(ExpandedVertex, "thickness") },
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT, .location = 4, .offset = @offsetOf(ExpandedVertex, "side") },
        .{ .buffer_slot = 0, .format = c.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT, .location = 5, .offset = @offsetOf(ExpandedVertex, "end") },
    };
    const vtx_bufs = [_]c.SDL_GPUVertexBufferDescription{.{
        .slot = 0,
        .pitch = STRIDE,
        .input_rate = c.SDL_GPU_VERTEXINPUTRATE_VERTEX,
        .instance_step_rate = 0,
    }};

    // Standard alpha blending so gizmo transparency works.
    var blend = std.mem.zeroes(c.SDL_GPUColorTargetBlendState);
    blend.enable_blend = true;
    blend.src_color_blendfactor = c.SDL_GPU_BLENDFACTOR_SRC_ALPHA;
    blend.dst_color_blendfactor = c.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
    blend.color_blend_op = c.SDL_GPU_BLENDOP_ADD;
    blend.src_alpha_blendfactor = c.SDL_GPU_BLENDFACTOR_ONE;
    blend.dst_alpha_blendfactor = c.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
    blend.alpha_blend_op = c.SDL_GPU_BLENDOP_ADD;
    const color_desc = c.SDL_GPUColorTargetDescription{
        .format = c.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
        .blend_state = blend,
    };

    var info = std.mem.zeroes(c.SDL_GPUGraphicsPipelineCreateInfo);
    info.vertex_shader = vert;
    info.fragment_shader = frag;
    info.primitive_type = c.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
    info.vertex_input_state = .{
        .num_vertex_buffers = 1,
        .vertex_buffer_descriptions = &vtx_bufs,
        .num_vertex_attributes = vtx_attrs.len,
        .vertex_attributes = &vtx_attrs,
    };
    info.rasterizer_state = .{
        .fill_mode = c.SDL_GPU_FILLMODE_FILL,
        .cull_mode = c.SDL_GPU_CULLMODE_NONE,
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
        .compare_op = c.SDL_GPU_COMPAREOP_LESS_OR_EQUAL,
        .back_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .front_stencil_state = std.mem.zeroes(c.SDL_GPUStencilOpState),
        .compare_mask = 0xff,
        .write_mask = 0xff,
        .enable_depth_test = depth_test,
        .enable_depth_write = false,
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

/// Ensure slot `i`'s vertex buffer holds at least `count` vertices.
fn ensureCapacity(dev: *c.SDL_GPUDevice, i: usize, count: usize) ?*c.SDL_GPUBuffer {
    if (state.gizmo_vtx_buf[i]) |buf| {
        if (state.gizmo_vtx_cap[i] >= count) return buf;
        c.SDL_ReleaseGPUBuffer(dev, buf);
        state.gizmo_vtx_buf[i] = null;
        state.gizmo_vtx_cap[i] = 0;
    }
    // Grow in chunks to avoid churn.
    const cap = std.mem.alignForward(usize, @max(count, 1024), 1024);
    const buf = c.SDL_CreateGPUBuffer(dev, &c.SDL_GPUBufferCreateInfo{
        .usage = c.SDL_GPU_BUFFERUSAGE_VERTEX,
        .size = @intCast(cap * STRIDE),
        .props = 0,
    }) orelse return null;
    state.gizmo_vtx_buf[i] = buf;
    state.gizmo_vtx_cap[i] = cap;
    return buf;
}

/// Draw the recorded gizmo `verts` (line-list pairs) over `color_tex` using the
/// supplied `view_proj`. `overlay` selects the always-on-top pipeline (for
/// manipulation handles); otherwise gizmos are depth-tested against the scene.
/// Must be called after `renderScene` (which stores the depth buffer) and
/// outside any open render pass.
pub fn renderGizmos(
    cmd: *c.SDL_GPUCommandBuffer,
    color_tex: *c.SDL_GPUTexture,
    w: u32,
    h: u32,
    view_proj: [16]f32,
    verts: []const GizmoVertex,
    overlay: bool,
) void {
    // Recorded as line-list pairs; an odd tail vertex has no partner to form a
    // segment, so ignore it.
    const segments = verts.len / 2;
    if (segments == 0) return;
    const dev = state.device orelse return;
    const depth_tex = state.depth_tex orelse return;
    const pl = (if (overlay) state.gizmo_overlay_pipeline else state.gizmo_pipeline) orelse return;

    const expanded_count = segments * CORNERS_PER_SEGMENT;
    const slot: usize = if (overlay) 1 else 0;
    const vbuf = ensureCapacity(dev, slot, expanded_count) orelse return;

    // Expand each segment into two triangles directly in the transfer buffer
    // (copy pass — outside the render pass).
    const bytes: u32 = @intCast(expanded_count * STRIDE);
    const tb = c.SDL_CreateGPUTransferBuffer(dev, &c.SDL_GPUTransferBufferCreateInfo{
        .usage = c.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
        .size = bytes,
        .props = 0,
    }) orelse return;
    defer c.SDL_ReleaseGPUTransferBuffer(dev, tb);
    {
        const p: [*]ExpandedVertex = @ptrCast(@alignCast(
            c.SDL_MapGPUTransferBuffer(dev, tb, false) orelse return,
        ));
        // Quad corners as (end, side): two A corners, two B corners, wound into
        // triangles (A-,A+,B-) and (B-,A+,B+). Cull is disabled, so order only
        // needs to cover the quad.
        const corners = [CORNERS_PER_SEGMENT][2]f32{
            .{ 0, -1 }, .{ 0, 1 }, .{ 1, -1 },
            .{ 1, -1 }, .{ 0, 1 }, .{ 1, 1 },
        };
        var s: usize = 0;
        while (s < segments) : (s += 1) {
            const va = verts[s * 2];
            const vb = verts[s * 2 + 1];
            for (corners, 0..) |corner, ci| {
                const at_b = corner[0] > 0.5;
                p[s * CORNERS_PER_SEGMENT + ci] = .{
                    .a = va.pos,
                    .b = vb.pos,
                    .color = if (at_b) vb.color else va.color,
                    .thickness = if (at_b) vb.thickness else va.thickness,
                    .side = corner[1],
                    .end = corner[0],
                };
            }
        }
        c.SDL_UnmapGPUTransferBuffer(dev, tb);
    }
    const cp = c.SDL_BeginGPUCopyPass(cmd) orelse return;
    c.SDL_UploadToGPUBuffer(cp, &c.SDL_GPUTransferBufferLocation{ .transfer_buffer = tb, .offset = 0 }, &c.SDL_GPUBufferRegion{ .buffer = vbuf, .offset = 0, .size = bytes }, false);
    c.SDL_EndGPUCopyPass(cp);

    // Load the existing color (the rendered scene) and depth; never clear.
    var color_info = std.mem.zeroes(c.SDL_GPUColorTargetInfo);
    color_info.texture = color_tex;
    color_info.load_op = c.SDL_GPU_LOADOP_LOAD;
    color_info.store_op = c.SDL_GPU_STOREOP_STORE;

    var depth_info = std.mem.zeroes(c.SDL_GPUDepthStencilTargetInfo);
    depth_info.texture = depth_tex;
    depth_info.load_op = c.SDL_GPU_LOADOP_LOAD;
    depth_info.store_op = c.SDL_GPU_STOREOP_STORE;

    const pass = c.SDL_BeginGPURenderPass(cmd, &color_info, 1, &depth_info) orelse return;
    defer c.SDL_EndGPURenderPass(pass);
    c.SDL_BindGPUGraphicsPipeline(pass, pl);
    c.SDL_SetGPUViewport(pass, &c.SDL_GPUViewport{
        .x = 0,
        .y = 0,
        .w = @floatFromInt(w),
        .h = @floatFromInt(h),
        .min_depth = 0.0,
        .max_depth = 1.0,
    });

    const ub = GizmoUB{
        .view_proj = view_proj,
        .viewport = .{ @floatFromInt(w), @floatFromInt(h) },
    };
    c.SDL_PushGPUVertexUniformData(cmd, 0, &ub, @sizeOf(GizmoUB));
    c.SDL_BindGPUVertexBuffers(pass, 0, &c.SDL_GPUBufferBinding{ .buffer = vbuf, .offset = 0 }, 1);
    c.SDL_DrawGPUPrimitives(pass, @intCast(expanded_count), 1, 0, 0);
}

/// Release gizmo GPU resources.
pub fn deinit(dev: *c.SDL_GPUDevice) void {
    for (&state.gizmo_vtx_buf, 0..) |*b, i| {
        if (b.*) |buf| c.SDL_ReleaseGPUBuffer(dev, buf);
        b.* = null;
        state.gizmo_vtx_cap[i] = 0;
    }
    if (state.gizmo_pipeline) |p| c.SDL_ReleaseGPUGraphicsPipeline(dev, p);
    if (state.gizmo_overlay_pipeline) |p| c.SDL_ReleaseGPUGraphicsPipeline(dev, p);
    state.gizmo_pipeline = null;
    state.gizmo_overlay_pipeline = null;
}
