const std = @import("std");
const Transform = @import("Transform.zig").Transform;
const Component = @import("Component.zig").Component;

/// Maximum scene nodes per scene.
pub const MAX_OBJECTS = 128;
/// Maximum components per scene node.
pub const MAX_COMPONENTS = 16;
/// Maximum length of a scene node name.
pub const NAME_MAX = 64;
/// Length of a UUID string stored in guid_buf.
const GUID_LEN = 36;

/// A scene node with a transform and list of components.
pub const SceneNode = struct {
    name_buf: [NAME_MAX]u8 = std.mem.zeroes([NAME_MAX]u8),
    name_len: usize = 0,
    /// Stable GUID (UUID string, 36 bytes). Empty until assigned.
    guid_buf: [GUID_LEN]u8 = .{0} ** GUID_LEN,
    guid_len: usize = 0,
    /// Index of parent node, or -1 for root.
    parent: i32 = -1,
    /// Whether the node is active in the scene.
    active: bool = true,
    /// Local transform (position, rotation, scale).
    transform: Transform = .{},
    components: [MAX_COMPONENTS]Component = undefined,
    /// Number of active components.
    component_count: usize = 0,

    pub fn nameSlice(self: *const SceneNode) []const u8 {
        return self.name_buf[0..self.name_len];
    }

    pub fn setName(self: *SceneNode, n: []const u8) void {
        const len = @min(n.len, NAME_MAX);
        @memcpy(self.name_buf[0..len], n[0..len]);
        self.name_len = len;
    }

    /// Returns the stable GUID string, or empty if not yet assigned.
    pub fn guidSlice(self: *const SceneNode) []const u8 {
        return self.guid_buf[0..self.guid_len];
    }

    /// Stores a UUID string as this node's stable identity.
    pub fn setGuidStr(self: *SceneNode, s: []const u8) void {
        const len = @min(s.len, GUID_LEN);
        @memcpy(self.guid_buf[0..len], s[0..len]);
        self.guid_len = len;
    }

    pub fn addComponent(self: *SceneNode, c: Component) bool {
        if (self.component_count >= MAX_COMPONENTS) return false;
        self.components[self.component_count] = c;
        self.component_count += 1;
        return true;
    }

    pub fn removeComponent(self: *SceneNode, idx: usize) void {
        if (idx >= self.component_count) return;
        for (idx..self.component_count - 1) |i| {
            self.components[i] = self.components[i + 1];
        }
        self.component_count -= 1;
    }
};
