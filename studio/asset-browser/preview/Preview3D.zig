//! Shared building blocks for GPU-rendered asset previews: a key+fill light
//! rig (a single directional light left one side of matte objects pitch
//! black), mesh-node construction, and a reusable interactive orbit-drag
//! panel used by both `MaterialEditor`'s live preview and the Inspector's
//! model preview.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const GpuRenderer = @import("../../scene-view/GpuRenderer.zig");
const PreviewCamera = @import("PreviewCamera.zig");
const MeshBounds = @import("MeshBounds.zig");
const EditorState = @import("../../services/EditorState.zig");

/// Two-light rig (key + a softer, cooler fill from the opposite side) so a
/// previewed object always reads clearly instead of half-vanishing into the
/// unlit side under a single directional light.
pub fn keyFillLights() [2]engine.SceneNode {
    var key = engine.SceneNode{};
    key.transform.rotation = .{ .x = 40, .y = -35, .z = 0 };
    key.components[0] = .{ .light = .{ .kind = .directional, .intensity = 1.7 } };
    key.component_count = 1;

    var fill = engine.SceneNode{};
    fill.transform.rotation = .{ .x = -20, .y = 150, .z = 0 };
    fill.components[0] = .{ .light = .{
        .kind = .directional,
        .intensity = 0.75,
        .color_r = 0.85,
        .color_g = 0.9,
        .color_b = 1.0,
    } };
    fill.component_count = 1;

    return .{ key, fill };
}

pub fn meshNode(mesh_guid: []const u8, mat_guid: []const u8) engine.SceneNode {
    var n = engine.SceneNode{};
    var mr: engine.MeshRendererComponent = .{};
    mr.mesh.set(mesh_guid);
    mr.material.set(mat_guid);
    n.components[0] = .{ .mesh_renderer = mr };
    n.component_count = 1;
    return n;
}

/// Framing radius that safely fits both embedded primitives — a cube's
/// bounding sphere (corner-to-center) is bigger than its face extent, so
/// framing to the sphere's 0.5 radius clipped the cube's corners. Using this
/// for both means swapping Sphere/Cube in a live preview never needs a
/// re-frame.
pub const primitive_frame_radius: f32 = 0.87;

/// An interactive, orbit-drag-to-look 3D preview panel: renders `nodes` each
/// frame into a `size`×`size` square. Left-drag orbits, scroll wheel zooms.
pub const Panel = struct {
    orbit: PreviewCamera.Orbit = .{},
    dragging: bool = false,
    key_buf: [512]u8 = undefined,
    key_len: usize = 0,

    /// Re-frame the camera when `key` (e.g. the loaded asset's path or GUID)
    /// changes; a no-op otherwise, so a user's orbiting isn't reset every
    /// frame just because the panel keeps redrawing the same asset.
    pub fn ensureFramed(self: *Panel, key: []const u8, target: engine.Vector3, radius: f32) void {
        if (self.key_len == key.len and std.mem.eql(u8, self.key_buf[0..self.key_len], key)) return;
        const n = @min(key.len, self.key_buf.len);
        @memcpy(self.key_buf[0..n], key[0..n]);
        self.key_len = n;
        self.orbit.frame(target, radius);
    }

    pub fn draw(self: *Panel, nodes: []const engine.SceneNode, size: u32) void {
        const target = GpuRenderer.renderPreview(nodes, self.orbit.pose(), size, size);

        var img_box = gui.box(@src(), .{}, .{
            .min_size_content = .{ .w = @floatFromInt(size), .h = @floatFromInt(size) },
            .gravity_x = 0.5,
            .background = true,
            .style = .content,
            .border = .all(1),
        });
        defer img_box.deinit();

        if (target) |t| {
            if (gui.Texture.fromTargetTemp(t) catch null) |tex| {
                _ = gui.image(@src(), .{ .source = .{ .texture = tex } }, .{ .expand = .both });
            }
        }

        self.handleInput(img_box);
    }

    fn handleInput(self: *Panel, box: *gui.BoxWidget) void {
        for (gui.events()) |*e| {
            switch (e.evt) {
                .mouse => |me| switch (me.action) {
                    .press => if (me.button == .left and gui.eventMatchSimple(e, box.data())) {
                        e.handle(@src(), box.data());
                        self.dragging = true;
                    },
                    .release => if (me.button == .left) {
                        self.dragging = false;
                    },
                    .motion => |delta| if (self.dragging) {
                        self.orbit.orbitBy(delta.x, delta.y, 0.3);
                    },
                    .wheel_y => |amt| if (gui.eventMatchSimple(e, box.data())) {
                        e.handle(@src(), box.data());
                        self.orbit.zoomBy(amt * 0.15);
                    },
                    else => {},
                },
                else => {},
            }
        }
    }
};

// ── Model live preview (`PreviewSystem.registerLiveProvider(.model, ...)`) ─

var model_panel: Panel = .{};

/// Interactive model preview: the mesh with its resolved default material
/// (mirrors the auto-assign logic in the mesh_renderer inspector field),
/// auto-framed to its bounds, orbit-drag-to-look. Matches `LiveDrawFn`
/// (`asset_path`, `guid`) so `PreviewSystem.drawLive` can call it directly.
pub fn drawPreview(asset_path: []const u8, guid: []const u8) void {
    _ = asset_path;
    const bounds = MeshBounds.local(guid) orelse return;
    const center = engine.Vector3{
        .x = (bounds.min.x + bounds.max.x) * 0.5,
        .y = (bounds.min.y + bounds.max.y) * 0.5,
        .z = (bounds.min.z + bounds.max.z) * 0.5,
    };
    const ext = engine.Vector3{ .x = bounds.max.x - bounds.min.x, .y = bounds.max.y - bounds.min.y, .z = bounds.max.z - bounds.min.z };
    const radius = @sqrt(ext.x * ext.x + ext.y * ext.y + ext.z * ext.z) * 0.5;

    var mat_buf: [36]u8 = undefined;
    const mat_guid = EditorState.modelPrimaryMaterial(gui.io, guid, &mat_buf) orelse engine.Material.presets[0].guid;

    model_panel.ensureFramed(guid, center, if (radius > 0.001) radius else 0.5);
    const lights = keyFillLights();
    const nodes = [_]engine.SceneNode{ lights[0], lights[1], meshNode(guid, mat_guid) };
    model_panel.draw(&nodes, 220);
}
