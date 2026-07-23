//! Runtime prefab spawning — `Instantiate`/`Destroy` API. Scripts queue
//! commands through `Frame`; the host flushes them after the update loop to
//! avoid invalidating the node slice mid-iteration. Template nodes are
//! `SceneNode` arrays (no editor/serde dependency in the shipped game).

const std = @import("std");
const SceneNode = @import("SceneNode.zig").SceneNode;
const MAX_OBJECTS = @import("SceneNode.zig").MAX_OBJECTS;
const GROWTH_CEILING = @import("SceneNode.zig").GROWTH_CEILING;
const Vector3 = @import("../root.zig").Vector3;

const GUID_LEN = 36;

pub const Spawner = struct {
    /// Maximum distinct prefab templates registered at once.
    pub const MAX_PREFABS = 256;
    /// Maximum spawn/destroy commands queued between flushes.
    pub const MAX_COMMANDS = 256;

    const Template = struct {
        guid: [GUID_LEN]u8 = .{0} ** GUID_LEN,
        guid_len: usize = 0,
        nodes: []SceneNode = &.{},

        fn guidSlice(self: *const Template) []const u8 {
            return self.guid[0..self.guid_len];
        }
    };

    const Command = union(enum) {
        instantiate: struct {
            prefab: [GUID_LEN]u8 = .{0} ** GUID_LEN,
            prefab_len: usize = 0,
            position: Vector3 = .{},
            rotation: Vector3 = .{},
            has_pos: bool = false,
            has_rot: bool = false,
        },
        destroy: struct {
            target: [GUID_LEN]u8 = .{0} ** GUID_LEN,
            target_len: usize = 0,
        },
    };

    /// Lazily resolve a prefab GUID into template nodes when it hasn't been
    /// pre-registered. Lets the shipped game pull a prefab from its asset package
    /// on first use instead of enumerating every prefab up front. Returns false
    /// if the GUID can't be resolved. Mirrors `SceneManager.Loader`.
    pub const Resolver = *const fn (ctx: ?*anyopaque, guid: []const u8, out: []SceneNode, out_count: *usize) bool;

    allocator: std.mem.Allocator,
    templates: [MAX_PREFABS]Template = [_]Template{.{}} ** MAX_PREFABS,
    template_count: usize = 0,
    commands: [MAX_COMMANDS]Command = undefined,
    command_count: usize = 0,
    resolve_fn: ?Resolver = null,
    resolve_ctx: ?*anyopaque = null,
    /// Index in the live buffer where the most recent flush began adding nodes,
    /// so the host can instantiate components for freshly spawned nodes.
    last_spawn_start: usize = 0,
    last_spawn_added: usize = 0,

    pub fn init(allocator: std.mem.Allocator) Spawner {
        return .{ .allocator = allocator };
    }

    pub fn deinit(self: *Spawner) void {
        for (self.templates[0..self.template_count]) |*t| {
            if (t.nodes.len != 0) self.allocator.free(t.nodes);
        }
        self.template_count = 0;
        self.command_count = 0;
    }

    // ── Template registry (host-facing) ─────────────────────────────────────

    /// Register (or replace) the template nodes for prefab `guid`. The nodes are
    /// copied; the caller keeps ownership of its buffer.
    pub fn registerPrefab(self: *Spawner, guid: []const u8, nodes: []const SceneNode) void {
        if (guid.len == 0) return;
        // Replace existing entry for this guid.
        for (self.templates[0..self.template_count]) |*t| {
            if (std.mem.eql(u8, t.guidSlice(), guid)) {
                const dup = self.allocator.dupe(SceneNode, nodes) catch return;
                if (t.nodes.len != 0) self.allocator.free(t.nodes);
                t.nodes = dup;
                return;
            }
        }
        if (self.template_count >= MAX_PREFABS) return;
        const dup = self.allocator.dupe(SceneNode, nodes) catch return;
        var t = &self.templates[self.template_count];
        const n = @min(guid.len, GUID_LEN);
        @memcpy(t.guid[0..n], guid[0..n]);
        t.guid_len = n;
        t.nodes = dup;
        self.template_count += 1;
    }

    /// Install a lazy prefab resolver (see `Resolver`).
    pub fn setResolver(self: *Spawner, resolver: Resolver, ctx: ?*anyopaque) void {
        self.resolve_fn = resolver;
        self.resolve_ctx = ctx;
    }

    fn findTemplate(self: *const Spawner, guid: []const u8) ?*const Template {
        for (self.templates[0..self.template_count]) |*t| {
            if (std.mem.eql(u8, t.guidSlice(), guid)) return t;
        }
        return null;
    }

    /// Find a registered template, or resolve+register it on demand. Null if the
    /// prefab is unknown and unresolvable. `Resolver`'s contract is "fill up to
    /// out.len, report the true count" (mirrors `SceneManager.Loader`) — if the
    /// scratch buffer fills exactly (ambiguous with truncation), it's grown and
    /// the resolver re-run, up to `GROWTH_CEILING`, so a large prefab (a
    /// Bistro-scale FBX hierarchy dragged into a scene) resolves correctly
    /// instead of silently truncating at `MAX_OBJECTS`.
    fn resolveTemplate(self: *Spawner, guid: []const u8) ?*const Template {
        if (self.findTemplate(guid)) |t| return t;
        const rf = self.resolve_fn orelse return null;

        var cap: usize = MAX_OBJECTS;
        var scratch = self.allocator.alloc(SceneNode, cap) catch return null;
        defer self.allocator.free(scratch);
        var c: usize = 0;
        while (true) {
            if (!rf(self.resolve_ctx, guid, scratch, &c)) return null;
            if (c < scratch.len or cap >= GROWTH_CEILING) break;
            cap = @min(cap *| 2, GROWTH_CEILING);
            const grown = self.allocator.alloc(SceneNode, cap) catch break;
            self.allocator.free(scratch);
            scratch = grown;
        }
        if (c == 0) return null;
        self.registerPrefab(guid, scratch[0..c]);
        return self.findTemplate(guid);
    }

    /// True if a template is registered for `guid`.
    pub fn hasPrefab(self: *const Spawner, guid: []const u8) bool {
        return self.findTemplate(guid) != null;
    }

    // ── Queueing (script-facing, via Frame) ─────────────────────────────────

    /// Queue a prefab instantiation. Applied on the next `flush`.
    pub fn instantiate(self: *Spawner, prefab_guid: []const u8, position: ?Vector3, rotation: ?Vector3) void {
        if (self.command_count >= MAX_COMMANDS or prefab_guid.len == 0) return;
        var cmd = Command{ .instantiate = .{} };
        const n = @min(prefab_guid.len, GUID_LEN);
        @memcpy(cmd.instantiate.prefab[0..n], prefab_guid[0..n]);
        cmd.instantiate.prefab_len = n;
        if (position) |p| {
            cmd.instantiate.position = p;
            cmd.instantiate.has_pos = true;
        }
        if (rotation) |r| {
            cmd.instantiate.rotation = r;
            cmd.instantiate.has_rot = true;
        }
        self.commands[self.command_count] = cmd;
        self.command_count += 1;
    }

    /// Queue destruction of the node (and its descendants) with `node_guid`.
    pub fn destroy(self: *Spawner, node_guid: []const u8) void {
        if (self.command_count >= MAX_COMMANDS or node_guid.len == 0) return;
        var cmd = Command{ .destroy = .{} };
        const n = @min(node_guid.len, GUID_LEN);
        @memcpy(cmd.destroy.target[0..n], node_guid[0..n]);
        cmd.destroy.target_len = n;
        self.commands[self.command_count] = cmd;
        self.command_count += 1;
    }

    /// Number of queued commands not yet flushed.
    pub fn pending(self: *const Spawner) usize {
        return self.command_count;
    }

    // ── Flush (host-facing) ──────────────────────────────────────────────────

    /// Apply all queued commands to the live node buffer `objects` (capacity
    /// `objects.len`, current length `count.*`). `io` supplies entropy for fresh
    /// instance GUIDs. Returns true if the node set changed. After a flush,
    /// `last_spawn_start`/`last_spawn_added` describe the range of newly added
    /// nodes so the host can bring their components to life.
    pub fn flush(self: *Spawner, io: std.Io, objects: []SceneNode, count: *usize) bool {
        var changed = false;
        self.last_spawn_added = 0;
        self.last_spawn_start = count.*;
        for (self.commands[0..self.command_count]) |*cmd| {
            switch (cmd.*) {
                .instantiate => |*c| {
                    if (self.applyInstantiate(io, c, objects, count)) changed = true;
                },
                .destroy => |*c| {
                    if (self.applyDestroy(c.target[0..c.target_len], objects, count)) changed = true;
                },
            }
        }
        self.command_count = 0;
        return changed;
    }

    fn applyInstantiate(self: *Spawner, io: std.Io, c: anytype, objects: []SceneNode, count: *usize) bool {
        const tmpl = self.resolveTemplate(c.prefab[0..c.prefab_len]) orelse return false;
        const tn = tmpl.nodes.len;
        if (tn == 0) return false;
        const base = count.*;
        if (base + tn > objects.len) return false;
        const offset: i32 = @intCast(base);

        for (tmpl.nodes, 0..) |src, i| {
            var node = src;
            node.parent = if (src.parent < 0) -1 else src.parent + offset;
            node.setPrefabNode(src.guidSlice());
            var gbuf: [GUID_LEN]u8 = undefined;
            node.setGuidStr(freshGuid(io, &gbuf));
            node.clearOverrides();
            node.prefab_source_len = 0;
            objects[base + i] = node;
        }
        objects[base].setPrefabSource(tmpl.guidSlice());
        if (c.has_pos) objects[base].transform.position = c.position;
        if (c.has_rot) objects[base].transform.rotation = c.rotation;

        count.* += tn;
        if (self.last_spawn_added == 0) self.last_spawn_start = base;
        self.last_spawn_added += tn;
        return true;
    }

    /// Remove the node with `guid` and all its descendants, compacting the array
    /// and remapping parent indices. Returns true if something was removed.
    fn applyDestroy(self: *Spawner, guid: []const u8, objects: []SceneNode, count: *usize) bool {
        const n = count.*;
        var target: ?usize = null;
        for (objects[0..n], 0..) |*o, i| {
            if (std.mem.eql(u8, o.guidSlice(), guid)) {
                target = i;
                break;
            }
        }
        const idx = target orelse return false;

        // Sized to `n` (the live count), not the `MAX_OBJECTS` default — a
        // fixed `[MAX_OBJECTS]bool`/`[MAX_OBJECTS]i32` here would index out of
        // bounds once a scene (e.g. a Bistro-scale FBX hierarchy) exceeds 128
        // live nodes. Cheap: bool/i32 arrays, not `SceneNode`-sized.
        const remove = self.allocator.alloc(bool, n) catch return false;
        defer self.allocator.free(remove);
        @memset(remove, false);
        remove[idx] = true;
        // Parents precede children in scene order; a forward pass propagates.
        var i: usize = 0;
        while (i < n) : (i += 1) {
            if (objects[i].parent >= 0 and remove[@intCast(objects[i].parent)]) remove[i] = true;
        }

        // Build old→new index map and compact IN PLACE (never touches
        // `objects` beyond `[0..n)`). Safe without a temp `SceneNode` buffer
        // because `map[k] <= k` always (compaction only ever shifts entries
        // toward index 0), so scanning `k` forward and writing
        // `objects[map[k]] = objects[k]` never clobbers a source index the
        // loop hasn't read yet.
        const map = self.allocator.alloc(i32, n) catch return false;
        defer self.allocator.free(map);
        @memset(map, -1);
        var next: usize = 0;
        for (0..n) |k| {
            if (!remove[k]) {
                map[k] = @intCast(next);
                next += 1;
            }
        }
        for (0..n) |k| {
            if (map[k] < 0) continue;
            var node = objects[k];
            if (node.parent >= 0) node.parent = map[@intCast(node.parent)];
            objects[@intCast(map[k])] = node;
        }
        count.* = next;
        return true;
    }

    /// Format a fresh random UUID-v4 string into `buf`.
    fn freshGuid(io: std.Io, buf: *[GUID_LEN]u8) []const u8 {
        var b: [16]u8 = undefined;
        io.randomSecure(&b) catch {};
        b[6] = (b[6] & 0x0F) | 0x40; // version 4
        b[8] = (b[8] & 0x3F) | 0x80; // variant
        _ = std.fmt.bufPrint(buf, "{x:0>2}{x:0>2}{x:0>2}{x:0>2}-{x:0>2}{x:0>2}-{x:0>2}{x:0>2}-{x:0>2}{x:0>2}-{x:0>2}{x:0>2}{x:0>2}{x:0>2}{x:0>2}{x:0>2}", .{
            b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15],
        }) catch unreachable;
        return buf[0..GUID_LEN];
    }
};

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

const testing = std.testing;

fn namedNode(name: []const u8, parent: i32, guid: []const u8) SceneNode {
    var n = SceneNode{};
    n.setName(name);
    n.parent = parent;
    n.setGuidStr(guid);
    return n;
}

test "register, instantiate with transform, and destroy" {
    var sp = Spawner.init(testing.allocator);
    defer sp.deinit();
    const io = testing.io;

    // A 2-node prefab template: root "Coin" + child "Glint".
    const tmpl = [_]SceneNode{
        namedNode("Coin", -1, "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        namedNode("Glint", 0, "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
    };
    sp.registerPrefab("11111111-1111-4111-8111-111111111111", &tmpl);
    try testing.expect(sp.hasPrefab("11111111-1111-4111-8111-111111111111"));

    // Heap-allocated: a `[MAX_OBJECTS]SceneNode` stack local overflows now that
    // the per-slot material table enlarged each node.
    const objects = try testing.allocator.alloc(SceneNode, MAX_OBJECTS);
    defer testing.allocator.free(objects);
    var count: usize = 0;

    sp.instantiate("11111111-1111-4111-8111-111111111111", .{ .x = 5, .y = 0, .z = 0 }, null);
    try testing.expectEqual(@as(usize, 1), sp.pending());
    try testing.expect(sp.flush(io, objects, &count));

    try testing.expectEqual(@as(usize, 2), count);
    try testing.expectEqualStrings("Coin", objects[0].nameSlice());
    try testing.expectEqualStrings("Glint", objects[1].nameSlice());
    try testing.expectEqual(@as(i32, -1), objects[0].parent);
    try testing.expectEqual(@as(i32, 0), objects[1].parent);
    // Spawn transform applied to the root.
    try testing.expectEqual(@as(f32, 5), objects[0].transform.position.x);
    // Linked to the prefab; fresh scene guid distinct from the template guid.
    try testing.expect(objects[0].isPrefabInstanceRoot());
    try testing.expectEqualStrings("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", objects[0].prefabNodeSlice());
    try testing.expect(!std.mem.eql(u8, objects[0].guidSlice(), "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    try testing.expectEqual(@as(usize, 2), sp.last_spawn_added);
    try testing.expectEqual(@as(usize, 0), sp.last_spawn_start);

    // Spawn a second instance under a different start index.
    sp.instantiate("11111111-1111-4111-8111-111111111111", null, null);
    _ = sp.flush(io, objects, &count);
    try testing.expectEqual(@as(usize, 4), count);
    try testing.expectEqual(@as(i32, 2), objects[3].parent); // child reparented by offset
    // Distinct instance identities.
    try testing.expect(!std.mem.eql(u8, objects[0].guidSlice(), objects[2].guidSlice()));

    // Destroy the first instance root → its child goes too.
    const root_guid = objects[0].guidSlice();
    var gbuf: [GUID_LEN]u8 = undefined;
    @memcpy(gbuf[0..root_guid.len], root_guid);
    sp.destroy(gbuf[0..root_guid.len]);
    try testing.expect(sp.flush(io, objects, &count));
    try testing.expectEqual(@as(usize, 2), count); // 4 - 2 removed
    // The surviving instance remains, with a valid parent chain.
    try testing.expectEqualStrings("Coin", objects[0].nameSlice());
    try testing.expectEqual(@as(i32, 0), objects[1].parent);
}

test "instantiate unknown prefab is a no-op" {
    var sp = Spawner.init(testing.allocator);
    defer sp.deinit();
    const objects = try testing.allocator.alloc(SceneNode, MAX_OBJECTS);
    defer testing.allocator.free(objects);
    var count: usize = 0;
    sp.instantiate("ffffffff-ffff-4fff-8fff-ffffffffffff", null, null);
    try testing.expect(!sp.flush(testing.io, objects, &count));
    try testing.expectEqual(@as(usize, 0), count);
}
