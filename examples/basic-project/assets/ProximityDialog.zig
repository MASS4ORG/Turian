const std = @import("std");
const engine = @import("engine");

/// World-affects-GUI demo (#47 showcase): shows the `ui.uidoc` "ProximityDialog"
/// node when the camera comes within `radius` of `target`'s world position,
/// hides it otherwise. The reverse data-flow from `WorldMarker` (which turns a
/// world position into a screen *position*); this turns a world distance into
/// UI *visibility*.
pub const ProximityDialog = struct {
    pub const is_component = true;

    /// The scene node carrying the `ui_document` component to update.
    hud: engine.GameObjectRef = .{},
    /// Stable GUID of the SceneNode whose proximity is being watched.
    target: engine.GameObjectRef = .{},
    /// Distance (metres) within which the dialog shows.
    radius: f32 = 3.0,

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

    fn findCameraPos(frame: engine.Frame) ?engine.Vector3 {
        for (frame.objects) |*obj| {
            if (!obj.active) continue;
            for (obj.components[0..obj.component_count]) |comp| {
                if (comp == .camera) return obj.transform.position;
            }
        }
        return null;
    }

    fn findTargetPos(self: *@This(), frame: engine.Frame) ?engine.Vector3 {
        const guid = self.target.slice();
        if (guid.len == 0) return null;
        for (frame.objects) |*obj| {
            if (!obj.active or !std.mem.eql(u8, obj.guidSlice(), guid)) continue;
            return obj.transform.position;
        }
        return null;
    }

    pub fn update(self: *@This(), frame: engine.Frame) void {
        const inst = self.resolveHud(frame) orelse return;
        if (self._node == null) self._node = inst.find("ProximityDialog");
        const node = self._node orelse return;

        const cam_pos = findCameraPos(frame) orelse return;
        const target_pos = self.findTargetPos(frame) orelse return;
        const near = cam_pos.subtract(target_pos).length() <= self.radius;
        inst.setActive(node, near);
    }
};
