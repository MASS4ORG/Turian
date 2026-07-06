//! Runtime UI document instance (C4): the game→UI data-flow half of the epic
//! (the reverse of D4's UI→game events). Scripts never attach to UI nodes;
//! the runtime exposes the loaded document instance and a small mutator set:
//!
//! ```zig
//! const hud = frame.uiDocument(node) orelse return;
//! if (hud.find("health_label")) |n| hud.setText(n, my_text);
//! ```
//!
//! `find` is O(nodes) — cache the result at awake-time, not per frame.
//! Pure data, zero dvui imports (D7); the per-frame draw stays in
//! `subsystems/ui_render/`.

const std = @import("std");
const document = @import("UiDocument.zig");
const events_mod = @import("UiEvents.zig");

pub const UiInstance = struct {
    /// The instantiated document. Owned: loaded via `UiDocument.loadFromBytes`
    /// with `allocator`, so string mutators can free/dupe per node.
    doc: document.UiDocument = .{},
    /// Load-time-resolved `EventId`s parallel to `doc.nodes`
    /// (see `UiEvents.resolveDocument`).
    resolved: []const ?events_mod.EventId = &.{},
    allocator: std.mem.Allocator,
    /// Mirrors the owning scene node/component's enabled state (C9): a
    /// disabled instance is kept but not drawn and receives no input.
    visible: bool = true,

    /// Load an instance from `.uidoc` bytes, resolving event bindings through
    /// `events` (strict: unresolved names warn and stay null, per D4).
    pub fn load(
        allocator: std.mem.Allocator,
        bytes: []const u8,
        events: *events_mod.UiEvents,
    ) !UiInstance {
        var doc = try document.UiDocument.loadFromBytes(allocator, bytes);
        errdefer doc.deinit(allocator);
        const resolved = try events.resolveDocument(allocator, &doc);
        return .{ .doc = doc, .resolved = resolved, .allocator = allocator };
    }

    pub fn deinit(self: *UiInstance) void {
        self.doc.deinit(self.allocator);
        if (self.resolved.len != 0) self.allocator.free(self.resolved);
        self.* = undefined;
    }

    /// Index of the first node named `name`, or null. O(nodes) — cache the
    /// result (e.g. look up once in `awake`).
    pub fn find(self: *const UiInstance, name: []const u8) ?usize {
        for (self.doc.nodes, 0..) |node, i| {
            if (std.mem.eql(u8, node.name, name)) return i;
        }
        return null;
    }

    /// Replace the text of `node`'s first `text` component (no-op if the
    /// index is invalid or the node has no text component).
    pub fn setText(self: *UiInstance, node: usize, text: []const u8) void {
        if (node >= self.doc.nodes.len) return;
        for (self.doc.nodes[node].components) |*c| {
            if (c.* != .text) continue;
            const copy = self.allocator.dupe(u8, text) catch return;
            self.allocator.free(c.text.text);
            c.text.text = copy;
            return;
        }
    }

    /// Show/hide a node (and its subtree — inactive nodes don't draw).
    pub fn setActive(self: *UiInstance, node: usize, active: bool) void {
        if (node >= self.doc.nodes.len) return;
        self.doc.nodes[node].active = active;
    }

    /// Set (or clear) the node's style tint.
    pub fn setTint(self: *UiInstance, node: usize, tint: ?[4]f32) void {
        if (node >= self.doc.nodes.len) return;
        self.doc.nodes[node].style.tint = tint;
    }

    /// Set (or clear) the node's explicit-position rect (D3's layout opt-out;
    /// C5 screen-anchored elements drive this from `worldToViewport`).
    pub fn setRect(self: *UiInstance, node: usize, rect: ?[4]f32) void {
        if (node >= self.doc.nodes.len) return;
        self.doc.nodes[node].item.rect = rect;
    }
};

/// Engine service owning every live `UiInstance`, keyed by the scene node
/// that carries the `ui_document` component. The host loop (generated game
/// main) populates it on scene load and draws from it each frame; scripts
/// reach instances through `frame.uiDocument(node)`.
pub const UiRuntime = struct {
    pub const MAX_INSTANCES = 16;

    const Entry = struct {
        node_guid: [64]u8 = undefined,
        node_guid_len: usize = 0,
        instance: UiInstance,
    };

    entries: [MAX_INSTANCES]Entry = undefined,
    count: usize = 0,

    pub fn init() UiRuntime {
        return .{};
    }

    pub fn deinitAll(self: *UiRuntime) void {
        for (self.entries[0..self.count]) |*e| e.instance.deinit();
        self.count = 0;
    }

    /// Register `instance` as owned by scene node `node_guid`. Returns false
    /// when the table is full (instance is NOT consumed then).
    pub fn add(self: *UiRuntime, node_guid: []const u8, instance: UiInstance) bool {
        if (self.count >= MAX_INSTANCES) return false;
        var e = Entry{ .instance = instance };
        e.node_guid_len = @min(node_guid.len, e.node_guid.len);
        @memcpy(e.node_guid[0..e.node_guid_len], node_guid[0..e.node_guid_len]);
        self.entries[self.count] = e;
        self.count += 1;
        return true;
    }

    /// Remove (and free) the instance owned by `node_guid`, if any (C9:
    /// node destroyed → instance freed; outstanding handles become invalid).
    pub fn remove(self: *UiRuntime, node_guid: []const u8) void {
        var i: usize = 0;
        while (i < self.count) : (i += 1) {
            const e = &self.entries[i];
            if (std.mem.eql(u8, e.node_guid[0..e.node_guid_len], node_guid)) {
                e.instance.deinit();
                self.entries[i] = self.entries[self.count - 1];
                self.count -= 1;
                return;
            }
        }
    }

    pub fn instanceFor(self: *UiRuntime, node_guid: []const u8) ?*UiInstance {
        for (self.entries[0..self.count]) |*e| {
            if (std.mem.eql(u8, e.node_guid[0..e.node_guid_len], node_guid)) return &e.instance;
        }
        return null;
    }

    /// Free (and remove) every instance whose node_guid is not in `keep`
    /// (C9: node destroyed / its scene unloaded -> instance freed). Linear
    /// scan against `keep` — fine at the "scene(s) changed" cadence the host
    /// loop calls this at, not a per-frame cost.
    pub fn retainOnly(self: *UiRuntime, keep: []const []const u8) void {
        var i: usize = 0;
        while (i < self.count) {
            const e = &self.entries[i];
            const guid = e.node_guid[0..e.node_guid_len];
            var found = false;
            for (keep) |k| {
                if (std.mem.eql(u8, k, guid)) {
                    found = true;
                    break;
                }
            }
            if (found) {
                i += 1;
            } else {
                e.instance.deinit();
                self.entries[i] = self.entries[self.count - 1];
                self.count -= 1;
            }
        }
    }

    /// All live instances, for the host loop's draw pass.
    pub fn instances(self: *UiRuntime) []Entry {
        return self.entries[0..self.count];
    }
};

// ── Tests ─────────────────────────────────────────────────────────────────────

const test_doc_json =
    \\{"nodes":[
    \\  {"guid":"root","name":"Root","parent":-1},
    \\  {"guid":"hp","name":"health_label","parent":0,
    \\   "components":[{"text":{"text":"HP 100"}}]}
    \\]}
;

test "load, find, and mutate a document instance (C4)" {
    const a = std.testing.allocator;
    var events = events_mod.UiEvents.init();
    var inst = try UiInstance.load(a, test_doc_json, &events);
    defer inst.deinit();

    try std.testing.expectEqual(@as(?usize, null), inst.find("nope"));
    const hp = inst.find("health_label").?;

    inst.setText(hp, "HP 75");
    try std.testing.expectEqualStrings("HP 75", inst.doc.nodes[hp].components[0].text.text);

    inst.setActive(hp, false);
    try std.testing.expect(!inst.doc.nodes[hp].active);

    inst.setTint(hp, .{ 1, 0, 0, 1 });
    try std.testing.expectEqual(@as(f32, 1), inst.doc.nodes[hp].style.tint.?[0]);

    inst.setRect(hp, .{ 10, 20, 100, 30 });
    try std.testing.expectEqual(@as(f32, 20), inst.doc.nodes[hp].item.rect.?[1]);
    inst.setRect(hp, null);
    try std.testing.expectEqual(@as(?[4]f32, null), inst.doc.nodes[hp].item.rect);
}

test "UiRuntime add/lookup/remove lifecycle (C9)" {
    const a = std.testing.allocator;
    var events = events_mod.UiEvents.init();
    var rt = UiRuntime.init();
    defer rt.deinitAll();

    const inst = try UiInstance.load(a, test_doc_json, &events);
    try std.testing.expect(rt.add("node-guid-1", inst));

    try std.testing.expect(rt.instanceFor("node-guid-1") != null);
    try std.testing.expect(rt.instanceFor("other") == null);

    rt.remove("node-guid-1");
    try std.testing.expect(rt.instanceFor("node-guid-1") == null);
}

test "UiRuntime.retainOnly frees instances not in the keep set" {
    const a = std.testing.allocator;
    var events = events_mod.UiEvents.init();
    var rt = UiRuntime.init();
    defer rt.deinitAll();

    try std.testing.expect(rt.add("keep-me", try UiInstance.load(a, test_doc_json, &events)));
    try std.testing.expect(rt.add("drop-me", try UiInstance.load(a, test_doc_json, &events)));

    rt.retainOnly(&.{"keep-me"});

    try std.testing.expect(rt.instanceFor("keep-me") != null);
    try std.testing.expect(rt.instanceFor("drop-me") == null);
    try std.testing.expectEqual(@as(usize, 1), rt.count);
}
