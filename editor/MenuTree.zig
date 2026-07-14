//! Generic runtime path-based menu tree builder: turns a
//! flat list of slash-separated "category/subcategory/name" strings into a
//! nested tree, so a cascaded (Unity-style) menu can be rendered without any
//! attribute/macro system — callers just declare where an entry lives in the
//! hierarchy and this module groups them. Deliberately has no GUI
//! dependency: it only knows about strings and an opaque `leaf` index that
//! callers map back to their own entry list.

const std = @import("std");

pub const Node = struct {
    name: []const u8,
    /// Index into the caller's entry list. Set only on leaves (nodes with no
    /// children) — a path segment can't currently be both a category and a
    /// creatable entry.
    leaf: ?usize = null,
    children: std.ArrayList(Node) = .empty,

    pub fn deinit(self: *Node, alloc: std.mem.Allocator) void {
        for (self.children.items) |*c| c.deinit(alloc);
        self.children.deinit(alloc);
    }

    fn findOrAddChild(self: *Node, alloc: std.mem.Allocator, name: []const u8) !*Node {
        for (self.children.items) |*c| {
            if (std.mem.eql(u8, c.name, name)) return c;
        }
        try self.children.append(alloc, .{ .name = name });
        return &self.children.items[self.children.items.len - 1];
    }
};

/// Build a tree from `paths`, where `paths[i]` is the menu path for entry
/// `i` (e.g. `"Material/Metal"`). A path with no `/` becomes a top-level
/// leaf. Menu order follows registration order, not alphabetical.
pub fn build(alloc: std.mem.Allocator, paths: []const []const u8) !Node {
    var root: Node = .{ .name = "" };
    for (paths, 0..) |path, i| {
        var node = &root;
        var it = std.mem.splitScalar(u8, path, '/');
        while (it.next()) |segment| {
            node = try node.findOrAddChild(alloc, segment);
        }
        node.leaf = i;
    }
    return root;
}

test "build groups entries by shared path prefixes" {
    const alloc = std.testing.allocator;
    var root = try build(alloc, &.{ "Folder", "Material/Metal", "Material/Plastic", "Data/PlayerStats" });
    defer root.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 3), root.children.items.len);

    try std.testing.expectEqualStrings("Folder", root.children.items[0].name);
    try std.testing.expectEqual(@as(?usize, 0), root.children.items[0].leaf);
    try std.testing.expectEqual(@as(usize, 0), root.children.items[0].children.items.len);

    const material = root.children.items[1];
    try std.testing.expectEqualStrings("Material", material.name);
    try std.testing.expectEqual(@as(?usize, null), material.leaf);
    try std.testing.expectEqual(@as(usize, 2), material.children.items.len);
    try std.testing.expectEqualStrings("Metal", material.children.items[0].name);
    try std.testing.expectEqual(@as(?usize, 1), material.children.items[0].leaf);
    try std.testing.expectEqualStrings("Plastic", material.children.items[1].name);
    try std.testing.expectEqual(@as(?usize, 2), material.children.items[1].leaf);

    const data = root.children.items[2];
    try std.testing.expectEqualStrings("Data", data.name);
    try std.testing.expectEqual(@as(usize, 1), data.children.items.len);
    try std.testing.expectEqualStrings("PlayerStats", data.children.items[0].name);
    try std.testing.expectEqual(@as(?usize, 3), data.children.items[0].leaf);
}

test {
    std.testing.refAllDecls(@This());
}
