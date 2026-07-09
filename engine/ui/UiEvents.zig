//! Typed UI event registry (D4): strings at rest in a serialized `.uidoc`
//! binding, dense integer handles at runtime (zero string compares per
//! frame), types in user code — no central enum to edit when adding an
//! event. Mirrors the type-keyed registry idiom already established by
//! `engine/Services.zig` (`register(T)`/`get(T)`).
//!
//! ```zig
//! pub const PlayClicked = struct {
//!     pub const event_name = "play_clicked"; // ties the type to the serialized name
//! };
//!
//! // in a user_script awake(frame):
//! const ev = frame.service(engine.ui.UiEvents).?;
//! ev.register(PlayClicked);      // name -> EventId interning
//! ev.on(PlayClicked, self, onPlay); // comptime type-keyed, like Services
//!
//! fn onPlay(self: *Self, _: PlayClicked) void { ... }
//! ```
//!
//! Load-time resolution: when a `.uidoc` (or the game) loads, each `named`
//! `EventBinding` resolves once through `resolveOrWarn` to a dense `EventId`;
//! dispatch after that is `fireId(id)` — an integer compare per subscriber,
//! no string work. v1 events are payload-less (`fireId`); `on`'s handler
//! signature already takes the event type as a payload parameter so adding
//! fields to an event later isn't an API break.

const std = @import("std");
const document = @import("UiDocument.zig");

pub const EventId = u32;
pub const INVALID_EVENT_ID: EventId = std.math.maxInt(EventId);

pub const UiEvents = struct {
    pub const MAX_EVENTS = 64;
    pub const MAX_SUBSCRIBERS = 128;
    const MAX_NAME_LEN = 64;

    const NameEntry = struct {
        buf: [MAX_NAME_LEN]u8 = undefined,
        len: usize = 0,

        fn slice(self: *const NameEntry) []const u8 {
            return self.buf[0..self.len];
        }
    };

    const Subscriber = struct {
        event_id: EventId,
        ctx: *anyopaque,
        thunk: *const fn (ctx: *anyopaque) void,
    };

    names: [MAX_EVENTS]NameEntry = undefined,
    name_count: usize = 0,
    subscribers: [MAX_SUBSCRIBERS]Subscriber = undefined,
    subscriber_count: usize = 0,

    pub fn init() UiEvents {
        return .{};
    }

    fn findName(self: *const UiEvents, name: []const u8) ?EventId {
        for (self.names[0..self.name_count], 0..) |*e, i| {
            if (std.mem.eql(u8, e.slice(), name)) return @intCast(i);
        }
        return null;
    }

    /// Intern `name`, returning its dense id. Idempotent: re-registering the
    /// same name returns the same id instead of duplicating it.
    pub fn registerName(self: *UiEvents, name: []const u8) EventId {
        if (self.findName(name)) |id| return id;
        std.debug.assert(self.name_count < MAX_EVENTS);
        const id: EventId = @intCast(self.name_count);
        var entry: NameEntry = .{};
        const n = @min(name.len, entry.buf.len);
        @memcpy(entry.buf[0..n], name[0..n]);
        entry.len = n;
        self.names[self.name_count] = entry;
        self.name_count += 1;
        return id;
    }

    /// Type-based registration: interns `E.event_name`. Comptime type-keyed,
    /// like `Services.register` — nothing central needs editing to add a new
    /// event; declare the struct wherever it belongs and register it.
    pub fn register(self: *UiEvents, comptime E: type) EventId {
        return self.registerName(E.event_name);
    }

    /// Resolve a serialized name to its dense id, or null if nothing has
    /// registered that name yet.
    pub fn resolve(self: *const UiEvents, name: []const u8) ?EventId {
        return self.findName(name);
    }

    /// Resolve `name`, logging a warning that lists every registered event
    /// name if it's unknown (D4: "Unknown name => load-time warning listing
    /// registered events"). Intended for `.uidoc` load-time binding
    /// resolution.
    pub fn resolveOrWarn(self: *const UiEvents, name: []const u8) ?EventId {
        if (self.findName(name)) |id| return id;
        var buf: [1024]u8 = undefined;
        var w: std.Io.Writer = .fixed(&buf);
        w.print("unknown UI event \"{s}\"; registered: ", .{name}) catch {};
        for (self.names[0..self.name_count], 0..) |*e, i| {
            if (i != 0) w.writeAll(", ") catch {};
            w.writeAll(e.slice()) catch {};
        }
        if (self.name_count == 0) w.writeAll("(none)") catch {};
        std.log.warn("{s}", .{w.buffered()});
        return null;
    }

    /// Load-time resolution (D4): resolves every `button` node's `named`
    /// `EventBinding` to an `EventId` ONCE, returning a slice parallel to
    /// `doc.nodes` (null entries = no button, or an unresolved name — already
    /// warned via `resolveOrWarn`). Callers (Studio's viewport overlay, the
    /// shipped game) dispatch clicks against this cache — `fireId` after this
    /// point is zero string work per frame, per the module doc comment.
    pub fn resolveDocument(self: *UiEvents, allocator: std.mem.Allocator, doc: *const document.UiDocument) ![]?EventId {
        var resolved = try allocator.alloc(?EventId, doc.nodes.len);
        for (doc.nodes, 0..) |node, i| {
            resolved[i] = null;
            for (node.components) |c| {
                if (c != .button) continue;
                switch (c.button.on_click) {
                    .named => |name| {
                        if (name.len == 0) continue;
                        resolved[i] = self.resolveOrWarn(name);
                    },
                    // Resolved at dispatch time instead (GameEventRegistry
                    // lookup by GUID, not a name-to-EventId intern) — see
                    // `ui_render.dispatchClicks`.
                    .channel => {},
                }
            }
        }
        return resolved;
    }

    /// Subscribe `handler` to `E`, invoked as `handler(ctx, E{})` whenever
    /// `E` fires by id. Registers `E` first if needed (idempotent). `ctx`
    /// must be a pointer whose storage outlives this registration.
    pub fn on(
        self: *UiEvents,
        comptime E: type,
        ctx: anytype,
        comptime handler: fn (@TypeOf(ctx), E) void,
    ) void {
        const id = self.register(E);
        const Ctx = @TypeOf(ctx);
        const Thunk = struct {
            fn call(erased: *anyopaque) void {
                handler(@as(Ctx, @ptrCast(@alignCast(erased))), E{});
            }
        };
        std.debug.assert(self.subscriber_count < MAX_SUBSCRIBERS);
        self.subscribers[self.subscriber_count] = .{
            .event_id = id,
            .ctx = @ptrCast(ctx),
            .thunk = &Thunk.call,
        };
        self.subscriber_count += 1;
    }

    /// Fire every subscriber registered for `id` — an integer compare per
    /// subscriber, zero string work. No-op for an invalid/unknown id.
    pub fn fireId(self: *const UiEvents, id: EventId) void {
        for (self.subscribers[0..self.subscriber_count]) |s| {
            if (s.event_id == id) s.thunk(s.ctx);
        }
    }
};

// ── Tests ─────────────────────────────────────────────────────────────────────

test "register interns a name and resolve finds it" {
    const PlayClicked = struct {
        pub const event_name = "play_clicked";
    };
    var ev = UiEvents.init();
    try std.testing.expectEqual(@as(?EventId, null), ev.resolve("play_clicked"));

    const id = ev.register(PlayClicked);
    try std.testing.expectEqual(id, ev.resolve("play_clicked").?);
    try std.testing.expectEqual(@as(?EventId, null), ev.resolve("nope"));
}

test "double-register the same event name is idempotent" {
    const PlayClicked = struct {
        pub const event_name = "play_clicked";
    };
    var ev = UiEvents.init();
    const id1 = ev.register(PlayClicked);
    const id2 = ev.register(PlayClicked);
    const id3 = ev.registerName("play_clicked");
    try std.testing.expectEqual(id1, id2);
    try std.testing.expectEqual(id1, id3);
    try std.testing.expectEqual(@as(usize, 1), ev.name_count);
}

test "fireId dispatches to subscribers of the matching event only" {
    const PlayClicked = struct {
        pub const event_name = "play_clicked";
    };
    const PauseClicked = struct {
        pub const event_name = "pause_clicked";
    };
    const Ctx = struct {
        play_calls: u32 = 0,
        pause_calls: u32 = 0,
        fn onPlay(self: *@This(), _: PlayClicked) void {
            self.play_calls += 1;
        }
        fn onPause(self: *@This(), _: PauseClicked) void {
            self.pause_calls += 1;
        }
    };

    var ev = UiEvents.init();
    var ctx = Ctx{};
    ev.on(PlayClicked, &ctx, Ctx.onPlay);
    ev.on(PauseClicked, &ctx, Ctx.onPause);

    const play_id = ev.resolve("play_clicked").?;
    ev.fireId(play_id);
    ev.fireId(play_id);
    try std.testing.expectEqual(@as(u32, 2), ctx.play_calls);
    try std.testing.expectEqual(@as(u32, 0), ctx.pause_calls);

    ev.fireId(ev.resolve("pause_clicked").?);
    try std.testing.expectEqual(@as(u32, 1), ctx.pause_calls);
}

test "fireId on an unregistered id is a no-op, never a crash" {
    var ev = UiEvents.init();
    ev.fireId(INVALID_EVENT_ID);
    ev.fireId(0);
}

test "multiple subscribers to the same event all fire" {
    const Ping = struct {
        pub const event_name = "ping";
    };
    const Ctx = struct {
        calls: u32 = 0,
        fn onPing(self: *@This(), _: Ping) void {
            self.calls += 1;
        }
    };
    var ev = UiEvents.init();
    var a = Ctx{};
    var b = Ctx{};
    ev.on(Ping, &a, Ctx.onPing);
    ev.on(Ping, &b, Ctx.onPing);

    ev.fireId(ev.resolve("ping").?);
    try std.testing.expectEqual(@as(u32, 1), a.calls);
    try std.testing.expectEqual(@as(u32, 1), b.calls);
}

test "resolveOrWarn returns null and does not crash for an unknown name" {
    const Known = struct {
        pub const event_name = "known_event";
    };
    var ev = UiEvents.init();
    _ = ev.register(Known);
    try std.testing.expectEqual(@as(?EventId, null), ev.resolveOrWarn("missing_event"));
    try std.testing.expect(ev.resolveOrWarn("known_event") != null);
}

test "resolveDocument resolves each button node's named binding once" {
    const a = std.testing.allocator;
    var button_components = [_]document.UiComponent{
        .{ .button = .{ .on_click = .{ .named = "play_clicked" } } },
    };
    var unresolved_components = [_]document.UiComponent{
        .{ .button = .{ .on_click = .{ .named = "missing" } } },
    };
    var nodes = [_]document.UiNode{
        .{ .guid = "panel", .components = &.{} }, // no button -> null
        .{ .guid = "btn", .components = &button_components },
        .{ .guid = "bad", .components = &unresolved_components },
    };
    const doc = document.UiDocument{ .nodes = &nodes };

    var ev = UiEvents.init();
    const play_id = ev.registerName("play_clicked");

    const resolved = try ev.resolveDocument(a, &doc);
    defer a.free(resolved);

    try std.testing.expectEqual(@as(usize, 3), resolved.len);
    try std.testing.expectEqual(@as(?EventId, null), resolved[0]);
    try std.testing.expectEqual(play_id, resolved[1].?);
    try std.testing.expectEqual(@as(?EventId, null), resolved[2]);
}
