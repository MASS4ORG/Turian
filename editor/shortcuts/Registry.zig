//! Runtime command table: id, display title, focus context, and default
//! bindings, with user overrides layered on top and persisted through the
//! same `editor.Settings` KV store every other editor preference uses (one
//! key per *overridden* command: `shortcut.<command_id>`).
//!
//! Registration mirrors `studio/main-window/Panels.zig`'s `registerCustom`:
//! `register()` is idempotent per id (re-registering replaces the
//! description, keeping any live override).
const std = @import("std");
const Settings = @import("../project/Settings.zig").Settings;
const binding = @import("Binding.zig");

pub const Binding = binding.Binding;
pub const Stroke = binding.Stroke;
pub const Key = binding.Key;

/// Focus scope a command is active in. `.global` overlaps every other
/// context for conflict purposes; two non-global contexts never overlap.
pub const Context = enum {
    global,
    scene_viewport,
    hierarchy,
    asset_browser,
    ui_editor,
    text_input,
};

pub const CommandDesc = struct {
    /// Namespaced, stable identifier ("edit.undo") — also the settings key
    /// suffix, so renaming it orphans any saved override.
    id: []const u8,
    /// Untranslated label; UI callers run it through their own `tr()`.
    title: []const u8,
    context: Context = .global,
    defaults: []const Binding = &.{},
    /// Whether this command requires its owning panel/widget to be actively
    /// engaged (dvui focus, or hover for a non-focusable surface like a 3D
    /// viewport) rather than merely visible. Studio callers compute
    /// "engaged" themselves and pass it to `Shortcuts.eventMatches`.
    requires_focus: bool = false,
};

/// Max simultaneous bindings a user override can hold for one command.
pub const MAX_OVERRIDE_BINDINGS = 4;

/// Fixed-capacity list of a command's override bindings.
pub const BoundOverride = struct {
    bindings: [MAX_OVERRIDE_BINDINGS]Binding = undefined,
    count: u8 = 0,

    pub fn slice(self: *const BoundOverride) []const Binding {
        return self.bindings[0..self.count];
    }
};

/// A user's rebind decision for one command, distinct from "no override"
/// (falls through to `CommandDesc.defaults`): `.unbound` means the user
/// explicitly cleared every binding.
pub const Override = union(enum) {
    unbound,
    bound: BoundOverride,
};

/// One registered command: its description plus any user override.
pub const Entry = struct {
    desc: CommandDesc,
    override: ?Override = null,
};

/// Two commands whose effective bindings collide in an overlapping context.
pub const Conflict = struct {
    a: []const u8,
    b: []const u8,
    binding: Binding,
};

const SETTINGS_KEY_PREFIX = "shortcut.";

pub const Registry = struct {
    allocator: std.mem.Allocator,
    entries: std.ArrayList(Entry) = .empty,

    pub fn init(allocator: std.mem.Allocator) Registry {
        return .{ .allocator = allocator };
    }

    pub fn deinit(self: *Registry) void {
        self.entries.deinit(self.allocator);
    }

    /// Registers commands, replacing the description of any id already
    /// present (a live override survives) and appending new ones.
    pub fn register(self: *Registry, descs: []const CommandDesc) !void {
        for (descs) |desc| {
            if (self.findIndex(desc.id)) |i| {
                self.entries.items[i].desc = desc;
            } else {
                try self.entries.append(self.allocator, .{ .desc = desc });
            }
        }
    }

    /// Every registered command, in registration order.
    pub fn commands(self: *const Registry) []const Entry {
        return self.entries.items;
    }

    /// The entry for `id`, or null if unregistered.
    pub fn find(self: *const Registry, id: []const u8) ?*const Entry {
        const i = self.findIndex(id) orelse return null;
        return &self.entries.items[i];
    }

    fn findIndex(self: *const Registry, id: []const u8) ?usize {
        for (self.entries.items, 0..) |entry, i| {
            if (std.mem.eql(u8, entry.desc.id, id)) return i;
        }
        return null;
    }

    /// The binding(s) in effect for `id`: the override if one is set,
    /// otherwise `CommandDesc.defaults`.
    pub fn effectiveBindings(self: *const Registry, id: []const u8) []const Binding {
        const entry = self.find(id) orelse return &.{};
        return effectiveBindingsFor(entry);
    }

    fn effectiveBindingsFor(entry: *const Entry) []const Binding {
        if (entry.override) |*ov| {
            return switch (ov.*) {
                .unbound => &.{},
                .bound => ov.bound.slice(),
            };
        }
        return entry.desc.defaults;
    }

    /// Copies `id`'s current effective bindings into a fresh `BoundOverride`
    /// — the seed for `addBinding`/`replaceBindingAt`/`removeBindingAt`,
    /// so mutating one binding doesn't discard the others.
    fn seedBound(entry: *const Entry) BoundOverride {
        if (entry.override) |ov| {
            return switch (ov) {
                .unbound => .{},
                .bound => |b| b,
            };
        }
        var b = BoundOverride{};
        for (entry.desc.defaults) |d| {
            if (b.count >= MAX_OVERRIDE_BINDINGS) break;
            b.bindings[b.count] = d;
            b.count += 1;
        }
        return b;
    }

    /// Sets, clears (`null`, reverts to default), or unbinds (`.unbound`)
    /// the in-memory override for `id`. No-op if `id` isn't registered.
    pub fn setOverride(self: *Registry, id: []const u8, override: ?Override) void {
        const i = self.findIndex(id) orelse return;
        self.entries.items[i].override = override;
    }

    /// Appends `binding` to `id`'s effective bindings.
    pub fn addBinding(self: *Registry, id: []const u8, new_binding: Binding) error{ Full, Duplicate }!void {
        const i = self.findIndex(id) orelse return;
        var b = seedBound(&self.entries.items[i]);
        for (b.slice()) |existing| if (existing.eql(new_binding)) return error.Duplicate;
        if (b.count >= MAX_OVERRIDE_BINDINGS) return error.Full;
        b.bindings[b.count] = new_binding;
        b.count += 1;
        self.entries.items[i].override = .{ .bound = b };
    }

    /// Replaces `id`'s binding at `index` in place. No-op if out of range.
    pub fn replaceBindingAt(self: *Registry, id: []const u8, index: usize, new_binding: Binding) void {
        const i = self.findIndex(id) orelse return;
        var b = seedBound(&self.entries.items[i]);
        if (index >= b.count) return;
        b.bindings[index] = new_binding;
        self.entries.items[i].override = .{ .bound = b };
    }

    /// Removes `id`'s binding at `index`, shifting the rest down. Dropping
    /// the last one becomes `.unbound`, not zero bindings with no override.
    pub fn removeBindingAt(self: *Registry, id: []const u8, index: usize) void {
        const i = self.findIndex(id) orelse return;
        var b = seedBound(&self.entries.items[i]);
        if (index >= b.count) return;
        for (index..b.count - 1) |k| b.bindings[k] = b.bindings[k + 1];
        b.count -= 1;
        self.entries.items[i].override = if (b.count == 0) .unbound else .{ .bound = b };
    }

    /// True when `id` has a live override (bound or unbound), as opposed to
    /// running on its code default.
    pub fn isOverridden(self: *const Registry, id: []const u8) bool {
        const entry = self.find(id) orelse return false;
        return entry.override != null;
    }

    /// Pairs of commands whose effective bindings collide in an overlapping
    /// context. Caller owns the returned slice.
    pub fn conflicts(self: *const Registry, allocator: std.mem.Allocator) ![]Conflict {
        var out: std.ArrayList(Conflict) = .empty;
        errdefer out.deinit(allocator);

        for (self.entries.items, 0..) |*ea, i| {
            const bindings_a = effectiveBindingsFor(ea);
            if (bindings_a.len == 0) continue;
            for (self.entries.items[i + 1 ..]) |*eb| {
                if (!contextsOverlap(ea.desc.context, eb.desc.context)) continue;
                const bindings_b = effectiveBindingsFor(eb);
                for (bindings_a) |a_bind| {
                    for (bindings_b) |b_bind| {
                        if (a_bind.eql(b_bind)) {
                            try out.append(allocator, .{ .a = ea.desc.id, .b = eb.desc.id, .binding = a_bind });
                        }
                    }
                }
            }
        }
        return out.toOwnedSlice(allocator);
    }

    fn contextsOverlap(a: Context, b: Context) bool {
        return a == b or a == .global or b == .global;
    }

    /// Populates in-memory overrides from the settings store: absent key
    /// keeps the code default, empty string means unbound, otherwise
    /// `|`-separated binding text (malformed entries are skipped).
    pub fn loadOverrides(self: *Registry, settings: *const Settings) void {
        var key_buf: [128]u8 = undefined;
        for (self.entries.items) |*entry| {
            const key = std.fmt.bufPrint(&key_buf, "{s}{s}", .{ SETTINGS_KEY_PREFIX, entry.desc.id }) catch continue;
            if (settings.get(key) == null) {
                entry.override = null;
                continue;
            }
            const raw = settings.getString(key, "");
            if (raw.len == 0) {
                entry.override = .unbound;
                continue;
            }
            var b = BoundOverride{};
            var it = std.mem.splitScalar(u8, raw, '|');
            while (it.next()) |part| {
                if (b.count >= MAX_OVERRIDE_BINDINGS) break;
                const parsed = Binding.parse(part) catch continue;
                b.bindings[b.count] = parsed;
                b.count += 1;
            }
            entry.override = if (b.count == 0) .unbound else .{ .bound = b };
        }
    }

    /// Writes every command's override state to the settings store: no
    /// override removes the key, `.unbound` writes an empty string,
    /// `.bound` writes `|`-separated binding text. Does not call
    /// `Settings.save` — caller persists to disk explicitly.
    pub fn saveOverrides(self: *const Registry, settings: *Settings) !void {
        var key_buf: [128]u8 = undefined;
        for (self.entries.items) |entry| {
            const key = try std.fmt.bufPrint(&key_buf, "{s}{s}", .{ SETTINGS_KEY_PREFIX, entry.desc.id });
            const override = entry.override orelse {
                settings.remove(key);
                continue;
            };
            switch (override) {
                .unbound => try settings.setString(key, ""),
                .bound => |b| {
                    var text_buf: [256]u8 = undefined;
                    var writer: std.Io.Writer = .fixed(&text_buf);
                    for (b.slice(), 0..) |bnd, idx| {
                        if (idx > 0) writer.writeByte('|') catch return error.OutOfMemory;
                        bnd.format(&writer) catch return error.OutOfMemory;
                    }
                    try settings.setString(key, writer.buffered());
                },
            }
        }
    }
};

test "register then effectiveBindings returns defaults" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();

    try reg.register(&.{.{
        .id = "edit.undo",
        .title = "Undo",
        .defaults = &.{Binding.single(.{ .key = .z, .ctrl = true })},
    }});

    const bindings = reg.effectiveBindings("edit.undo");
    try std.testing.expectEqual(1, bindings.len);
    try std.testing.expectEqual(Key.z, bindings[0].first.key);
    try std.testing.expect(!reg.isOverridden("edit.undo"));
}

test "setOverride bound replaces defaults" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    try reg.register(&.{.{ .id = "edit.undo", .title = "Undo", .defaults = &.{Binding.single(.{ .key = .z, .ctrl = true })} }});

    var b = BoundOverride{};
    b.bindings[0] = Binding.single(.{ .key = .u, .ctrl = true, .shift = true });
    b.count = 1;
    reg.setOverride("edit.undo", .{ .bound = b });

    const bindings = reg.effectiveBindings("edit.undo");
    try std.testing.expectEqual(1, bindings.len);
    try std.testing.expectEqual(Key.u, bindings[0].first.key);
    try std.testing.expect(reg.isOverridden("edit.undo"));
}

test "setOverride unbound clears effective bindings" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    try reg.register(&.{.{ .id = "edit.undo", .title = "Undo", .defaults = &.{Binding.single(.{ .key = .z, .ctrl = true })} }});

    reg.setOverride("edit.undo", .unbound);

    try std.testing.expectEqual(0, reg.effectiveBindings("edit.undo").len);
    try std.testing.expect(reg.isOverridden("edit.undo"));
}

test "setOverride null reverts to default" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    try reg.register(&.{.{ .id = "edit.undo", .title = "Undo", .defaults = &.{Binding.single(.{ .key = .z, .ctrl = true })} }});
    reg.setOverride("edit.undo", .unbound);

    reg.setOverride("edit.undo", null);

    const bindings = reg.effectiveBindings("edit.undo");
    try std.testing.expectEqual(1, bindings.len);
    try std.testing.expect(!reg.isOverridden("edit.undo"));
}

test "re-registering preserves a live override" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    try reg.register(&.{.{ .id = "edit.undo", .title = "Undo", .defaults = &.{Binding.single(.{ .key = .z, .ctrl = true })} }});
    reg.setOverride("edit.undo", .unbound);

    try reg.register(&.{.{ .id = "edit.undo", .title = "Undo (rescanned)", .defaults = &.{Binding.single(.{ .key = .z, .ctrl = true })} }});

    try std.testing.expect(reg.isOverridden("edit.undo"));
    try std.testing.expectEqualStrings("Undo (rescanned)", reg.find("edit.undo").?.desc.title);
}

test "addBinding appends to defaults without discarding them" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    try reg.register(&.{.{ .id = "edit.redo", .title = "Redo", .defaults = &.{Binding.single(.{ .key = .y, .ctrl = true })} }});

    try reg.addBinding("edit.redo", Binding.single(.{ .key = .z, .ctrl = true, .shift = true }));

    const bindings = reg.effectiveBindings("edit.redo");
    try std.testing.expectEqual(2, bindings.len);
    try std.testing.expectEqual(Key.y, bindings[0].first.key);
    try std.testing.expectEqual(Key.z, bindings[1].first.key);
}

test "addBinding rejects a duplicate" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    const b = Binding.single(.{ .key = .y, .ctrl = true });
    try reg.register(&.{.{ .id = "edit.redo", .title = "Redo", .defaults = &.{b} }});

    try std.testing.expectError(error.Duplicate, reg.addBinding("edit.redo", b));
}

test "addBinding rejects past capacity" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    try reg.register(&.{.{ .id = "edit.redo", .title = "Redo" }});

    for (0..MAX_OVERRIDE_BINDINGS) |i| {
        try reg.addBinding("edit.redo", Binding.single(.{ .key = @enumFromInt(@intFromEnum(Key.a) + i) }));
    }
    try std.testing.expectError(error.Full, reg.addBinding("edit.redo", Binding.single(.{ .key = .z })));
}

test "removeBindingAt drops one binding and keeps the rest" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    try reg.register(&.{.{ .id = "edit.redo", .title = "Redo", .defaults = &.{
        Binding.single(.{ .key = .y, .ctrl = true }),
        Binding.single(.{ .key = .z, .ctrl = true, .shift = true }),
    } }});

    reg.removeBindingAt("edit.redo", 0);

    const bindings = reg.effectiveBindings("edit.redo");
    try std.testing.expectEqual(1, bindings.len);
    try std.testing.expectEqual(Key.z, bindings[0].first.key);
}

test "removeBindingAt down to zero becomes unbound" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    try reg.register(&.{.{ .id = "edit.undo", .title = "Undo", .defaults = &.{Binding.single(.{ .key = .z, .ctrl = true })} }});

    reg.removeBindingAt("edit.undo", 0);

    try std.testing.expectEqual(0, reg.effectiveBindings("edit.undo").len);
    try std.testing.expect(reg.isOverridden("edit.undo"));
}

test "conflicts: same context, same binding" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    const b = Binding.single(.{ .key = .s, .ctrl = true });
    try reg.register(&.{
        .{ .id = "a", .title = "A", .context = .hierarchy, .defaults = &.{b} },
        .{ .id = "b", .title = "B", .context = .hierarchy, .defaults = &.{b} },
    });

    const found = try reg.conflicts(std.testing.allocator);
    defer std.testing.allocator.free(found);
    try std.testing.expectEqual(1, found.len);
}

test "conflicts: disjoint contexts do not collide" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    const b = Binding.single(.{ .key = .s, .ctrl = true });
    try reg.register(&.{
        .{ .id = "a", .title = "A", .context = .hierarchy, .defaults = &.{b} },
        .{ .id = "b", .title = "B", .context = .asset_browser, .defaults = &.{b} },
    });

    const found = try reg.conflicts(std.testing.allocator);
    defer std.testing.allocator.free(found);
    try std.testing.expectEqual(0, found.len);
}

test "conflicts: global overlaps every context" {
    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    const b = Binding.single(.{ .key = .s, .ctrl = true });
    try reg.register(&.{
        .{ .id = "a", .title = "A", .context = .global, .defaults = &.{b} },
        .{ .id = "b", .title = "B", .context = .asset_browser, .defaults = &.{b} },
    });

    const found = try reg.conflicts(std.testing.allocator);
    defer std.testing.allocator.free(found);
    try std.testing.expectEqual(1, found.len);
}

test "save then load overrides round-trips through Settings" {
    var settings = try Settings.init(std.testing.allocator, "/tmp/turian-shortcuts-registry-test", null);
    defer settings.deinit();

    var reg = Registry.init(std.testing.allocator);
    defer reg.deinit();
    try reg.register(&.{
        .{ .id = "edit.undo", .title = "Undo", .defaults = &.{Binding.single(.{ .key = .z, .ctrl = true })} },
        .{ .id = "edit.redo", .title = "Redo", .defaults = &.{Binding.single(.{ .key = .y, .ctrl = true })} },
        .{ .id = "edit.copy", .title = "Copy", .defaults = &.{Binding.single(.{ .key = .c, .ctrl = true })} },
    });
    var undo_bound = BoundOverride{};
    undo_bound.bindings[0] = Binding.single(.{ .key = .u, .ctrl = true });
    undo_bound.count = 1;
    reg.setOverride("edit.undo", .{ .bound = undo_bound });
    reg.setOverride("edit.redo", .unbound);
    try reg.addBinding("edit.copy", Binding.single(.{ .key = .insert, .ctrl = true }));

    try reg.saveOverrides(&settings);

    var reloaded = Registry.init(std.testing.allocator);
    defer reloaded.deinit();
    try reloaded.register(&.{
        .{ .id = "edit.undo", .title = "Undo", .defaults = &.{Binding.single(.{ .key = .z, .ctrl = true })} },
        .{ .id = "edit.redo", .title = "Redo", .defaults = &.{Binding.single(.{ .key = .y, .ctrl = true })} },
        .{ .id = "edit.copy", .title = "Copy", .defaults = &.{Binding.single(.{ .key = .c, .ctrl = true })} },
    });
    reloaded.loadOverrides(&settings);

    const undo_bindings = reloaded.effectiveBindings("edit.undo");
    try std.testing.expectEqual(1, undo_bindings.len);
    try std.testing.expectEqual(Key.u, undo_bindings[0].first.key);

    try std.testing.expectEqual(0, reloaded.effectiveBindings("edit.redo").len);
    try std.testing.expect(reloaded.isOverridden("edit.redo"));

    const copy_bindings = reloaded.effectiveBindings("edit.copy");
    try std.testing.expectEqual(2, copy_bindings.len);
    try std.testing.expectEqual(Key.c, copy_bindings[0].first.key);
    try std.testing.expectEqual(Key.insert, copy_bindings[1].first.key);
}

test {
    std.testing.refAllDecls(@This());
}
