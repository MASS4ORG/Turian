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

/// Maximum number of prefab override group keys per node (issue #32).
pub const MAX_OVERRIDES = 8;
/// Maximum length of a prefab override group key (e.g. "transform").
pub const OVERRIDE_KEY_MAX = 16;

/// Prefab override groups (issue #32). A node only stores the *keys* of the
/// groups it has overridden; each key means "this instance changed this group,
/// so keep it when the source prefab propagates". Group granularity (rather than
/// per-field) keeps the fixed-size node tractable and is correct for propagation.
pub const OverrideGroup = enum {
    name,
    active,
    transform,
    components,

    pub fn key(self: OverrideGroup) []const u8 {
        return @tagName(self);
    }
};

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

    // ── Prefab linkage (issue #32) ──────────────────────────────────────────
    /// Source prefab asset GUID. Non-empty **only on a prefab-instance root**;
    /// its presence marks this node as the root of a prefab instance.
    prefab_source_buf: [GUID_LEN]u8 = .{0} ** GUID_LEN,
    prefab_source_len: usize = 0,
    /// GUID of the corresponding template node within the prefab. Set on **every**
    /// node belonging to a prefab instance (root + descendants); matches instance
    /// nodes back to their template for propagate / revert.
    prefab_node_buf: [GUID_LEN]u8 = .{0} ** GUID_LEN,
    prefab_node_len: usize = 0,
    /// Overridden group keys (see `OverrideGroup`). Preserved across propagation.
    overrides: [MAX_OVERRIDES][OVERRIDE_KEY_MAX]u8 = std.mem.zeroes([MAX_OVERRIDES][OVERRIDE_KEY_MAX]u8),
    override_lens: [MAX_OVERRIDES]u8 = .{0} ** MAX_OVERRIDES,
    override_count: usize = 0,

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

    // ── Prefab linkage helpers (issue #32) ──────────────────────────────────

    /// Source prefab asset GUID, or empty for non-instance-root nodes.
    pub fn prefabSourceSlice(self: *const SceneNode) []const u8 {
        return self.prefab_source_buf[0..self.prefab_source_len];
    }

    pub fn setPrefabSource(self: *SceneNode, s: []const u8) void {
        const len = @min(s.len, GUID_LEN);
        @memcpy(self.prefab_source_buf[0..len], s[0..len]);
        self.prefab_source_len = len;
    }

    /// Template-node GUID, or empty for nodes outside any prefab instance.
    pub fn prefabNodeSlice(self: *const SceneNode) []const u8 {
        return self.prefab_node_buf[0..self.prefab_node_len];
    }

    pub fn setPrefabNode(self: *SceneNode, s: []const u8) void {
        const len = @min(s.len, GUID_LEN);
        @memcpy(self.prefab_node_buf[0..len], s[0..len]);
        self.prefab_node_len = len;
    }

    /// True when this node is the root of a prefab instance.
    pub fn isPrefabInstanceRoot(self: *const SceneNode) bool {
        return self.prefab_source_len != 0;
    }

    /// True when this node belongs to a prefab instance (root or descendant).
    pub fn isPartOfPrefab(self: *const SceneNode) bool {
        return self.prefab_node_len != 0;
    }

    /// True when `group` is recorded as overridden on this node.
    pub fn hasOverride(self: *const SceneNode, group: OverrideGroup) bool {
        const k = group.key();
        for (0..self.override_count) |i| {
            if (std.mem.eql(u8, self.overrides[i][0..self.override_lens[i]], k)) return true;
        }
        return false;
    }

    /// Record `group` as overridden. No-op if already present or at capacity.
    pub fn addOverride(self: *SceneNode, group: OverrideGroup) void {
        if (self.hasOverride(group)) return;
        if (self.override_count >= MAX_OVERRIDES) return;
        const k = group.key();
        const len = @min(k.len, OVERRIDE_KEY_MAX);
        @memcpy(self.overrides[self.override_count][0..len], k[0..len]);
        self.override_lens[self.override_count] = @intCast(len);
        self.override_count += 1;
    }

    /// Record an override by its group key string (used when deserializing).
    /// Unknown keys are ignored.
    pub fn addOverrideKey(self: *SceneNode, key: []const u8) void {
        if (std.meta.stringToEnum(OverrideGroup, key)) |g| self.addOverride(g);
    }

    /// Drop all recorded overrides.
    pub fn clearOverrides(self: *SceneNode) void {
        self.override_count = 0;
    }

    /// Drop all prefab linkage (source, template node, overrides), turning a
    /// prefab instance node back into a plain scene node ("unpack").
    pub fn clearPrefabLink(self: *SceneNode) void {
        self.prefab_source_len = 0;
        self.prefab_node_len = 0;
        self.override_count = 0;
    }
};

test "prefab linkage and override bookkeeping" {
    var n = SceneNode{};
    try std.testing.expect(!n.isPartOfPrefab());
    try std.testing.expect(!n.isPrefabInstanceRoot());

    n.setPrefabSource("3a13016e-4d55-40fc-8b22-5b4f1c3a9d12");
    n.setPrefabNode("ffffffff-ffff-4fff-bfff-ffffffffffff");
    try std.testing.expect(n.isPrefabInstanceRoot());
    try std.testing.expect(n.isPartOfPrefab());
    try std.testing.expectEqualStrings("3a13016e-4d55-40fc-8b22-5b4f1c3a9d12", n.prefabSourceSlice());

    try std.testing.expect(!n.hasOverride(.transform));
    n.addOverride(.transform);
    n.addOverride(.transform); // idempotent
    n.addOverride(.name);
    try std.testing.expect(n.hasOverride(.transform));
    try std.testing.expect(n.hasOverride(.name));
    try std.testing.expect(!n.hasOverride(.components));
    try std.testing.expectEqual(@as(usize, 2), n.override_count);

    n.clearPrefabLink();
    try std.testing.expect(!n.isPartOfPrefab());
    try std.testing.expect(!n.isPrefabInstanceRoot());
    try std.testing.expectEqual(@as(usize, 0), n.override_count);
}
