const std = @import("std");
const engine = @import("engine");

/// C5 demo: screen-anchored world element (an enemy health bar / name plate
/// pattern). Projects this node's world position into the HUD document's
/// reference-resolution space every frame (`engine.Projection.worldToViewport`)
/// and repositions a named UI node there via the C4 instance API's `setRect`
/// (D3's explicit-position opt-out, reserved for exactly this). Hides the
/// marker when the tracked point goes behind the camera.
pub const WorldMarker = struct {
    pub const is_component = true;

    /// The scene node carrying the `ui_document` component to update.
    hud: engine.GameObjectRef = .{},
    /// Marker size in the document's reference-resolution units.
    size: engine.Vector2 = .{ .x = 140, .y = 28 },

    _node: ?usize = null,

    fn resolveHud(self: *@This(), frame: engine.Frame) ?*engine.ui.UiInstance {
        const guid = self.hud.slice();
        if (guid.len == 0) return null;
        for (frame.objects) |*obj| {
            if (!std.mem.eql(u8, obj.guidSlice(), guid)) continue;
            return frame.uiDocument(obj);
        }
        return null;
    }

    const CameraPose = struct { pos: engine.Vector3, rot: engine.Vector3, fov: f32, near: f32, far: f32 };

    /// First active camera found in the scene — mirrors
    /// `subsystems/render/`'s own scene-camera search, independently,
    /// since scripts don't link `render`/`gpu`.
    fn findCamera(frame: engine.Frame) ?CameraPose {
        for (frame.objects) |*obj| {
            if (!obj.active) continue;
            for (obj.components[0..obj.component_count]) |comp| {
                if (comp == .camera) {
                    return .{
                        .pos = obj.transform.position,
                        .rot = obj.transform.rotation,
                        .fov = comp.camera.fov,
                        .near = comp.camera.near,
                        .far = comp.camera.far,
                    };
                }
            }
        }
        return null;
    }

    pub fn update(self: *@This(), frame: engine.Frame) void {
        const inst = self.resolveHud(frame) orelse return;
        if (self._node == null) self._node = inst.find("EnemyMarker");
        const node = self._node orelse return;

        const cam = findCamera(frame) orelse {
            inst.setActive(node, false);
            return;
        };

        // Reference resolution matches the document's own (1920x1080) —
        // `ui_render`'s scale_mode handles scaling the rect to the real window.
        const view_proj = engine.Projection.cameraViewProj(cam.pos, cam.rot, cam.fov, 1920.0 / 1080.0, cam.near, cam.far);
        const world_pos = frame.transform.position;
        const screen = engine.Projection.worldToViewport(view_proj, .{ 0, 0, 1920, 1080 }, world_pos) orelse {
            inst.setActive(node, false); // behind the camera
            return;
        };

        inst.setActive(node, true);
        inst.setRect(node, .{ screen[0] - self.size.x / 2, screen[1] - self.size.y, self.size.x, self.size.y });
    }
};
