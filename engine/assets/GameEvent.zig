//! GameEvent — an Inspector-wireable event-channel DataAsset.
//! A publisher (e.g. a UI button's `channel` binding)
//! and any number of subscribers reference the SAME asset GUID — never each
//! other directly — and share one live instance via `GameEventRegistry`.
//! Mirrors `engine.ui.UiEvents`' typed dispatch, but per-instance (one
//! channel = one asset) rather than per-name.
//!
//! ```zig
//! // in a subscriber's awake(frame):
//! const door = frame.gameEvent(self.open_channel) orelse return;
//! door.on(self, onDoorOpen);
//!
//! fn onDoorOpen(self: *Self) void { ... }
//!
//! // in a publisher (e.g. a button's on_click: {channel: "<guid>"} fires
//! // this automatically via ui_render.dispatchClicks — no script needed):
//! frame.gameEvent(self.open_channel).?.raise();
//! ```

const std = @import("std");

pub const GameEvent = struct {
    pub const MAX_SUBSCRIBERS = 16;

    const Subscriber = struct {
        ctx: *anyopaque,
        thunk: *const fn (ctx: *anyopaque) void,
    };

    subscribers: [MAX_SUBSCRIBERS]Subscriber = undefined,
    subscriber_count: usize = 0,

    pub fn init() GameEvent {
        return .{};
    }

    /// Subscribe `handler` to this channel, invoked as `handler(ctx)` whenever
    /// `raise` is called. `ctx` must be a pointer whose storage outlives this
    /// registration.
    pub fn on(self: *GameEvent, ctx: anytype, comptime handler: fn (@TypeOf(ctx)) void) void {
        const Ctx = @TypeOf(ctx);
        const Thunk = struct {
            fn call(erased: *anyopaque) void {
                handler(@as(Ctx, @ptrCast(@alignCast(erased))));
            }
        };
        std.debug.assert(self.subscriber_count < MAX_SUBSCRIBERS);
        self.subscribers[self.subscriber_count] = .{ .ctx = @ptrCast(ctx), .thunk = &Thunk.call };
        self.subscriber_count += 1;
    }

    /// Notify every subscriber. No-op (never a crash) if nobody's listening
    /// yet — a channel with no subscribers is a normal, valid state (e.g. the
    /// listener hasn't `awake`d yet, or nothing cares).
    pub fn raise(self: *const GameEvent) void {
        for (self.subscribers[0..self.subscriber_count]) |s| s.thunk(s.ctx);
    }
};

/// Runtime registry holding one shared `GameEvent` instance per asset GUID
/// — a publisher and a subscriber
/// that both reference the same asset GUID resolve to the SAME instance,
/// decoupled from each other. Fixed-capacity, linear-scan — mirrors
/// `engine.ui.UiRuntime`'s shape (registered into `Services`/`Frame` the same
/// way: `g_services.register(GameEventRegistry, &g_game_events)`, fetched via
/// `frame.gameEvent(ref)`).
pub const GameEventRegistry = struct {
    pub const MAX_CHANNELS = 32;

    const Entry = struct {
        guid_buf: [64]u8 = undefined,
        guid_len: usize = 0,
        event: GameEvent = .{},
    };

    entries: [MAX_CHANNELS]Entry = undefined,
    count: usize = 0,

    pub fn init() GameEventRegistry {
        return .{};
    }

    /// Returns the shared instance for `guid`, creating it (idempotently) on
    /// first reference. Null once `MAX_CHANNELS` distinct channels are live —
    /// callers treat a missing channel as "nobody's listening" (see `raise`'s
    /// no-op-on-no-subscribers contract), so this degrades gracefully.
    pub fn getOrCreate(self: *GameEventRegistry, guid: []const u8) ?*GameEvent {
        for (self.entries[0..self.count]) |*e| {
            if (std.mem.eql(u8, e.guid_buf[0..e.guid_len], guid)) return &e.event;
        }
        if (self.count >= MAX_CHANNELS) return null;
        const e = &self.entries[self.count];
        e.* = .{};
        const n = @min(guid.len, e.guid_buf.len);
        @memcpy(e.guid_buf[0..n], guid[0..n]);
        e.guid_len = n;
        self.count += 1;
        return &e.event;
    }
};

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test "raise with no subscribers is a no-op, never a crash" {
    const ev = GameEvent.init();
    ev.raise();
}

test "on then raise invokes every subscriber" {
    var ev = GameEvent.init();
    const Ctx = struct {
        calls: u32 = 0,
        fn onRaised(self: *@This()) void {
            self.calls += 1;
        }
    };
    var a = Ctx{};
    var b = Ctx{};
    ev.on(&a, Ctx.onRaised);
    ev.on(&b, Ctx.onRaised);

    ev.raise();
    try std.testing.expectEqual(@as(u32, 1), a.calls);
    try std.testing.expectEqual(@as(u32, 1), b.calls);

    ev.raise();
    try std.testing.expectEqual(@as(u32, 2), a.calls);
}

test "registry shares one instance per GUID" {
    var reg = GameEventRegistry.init();
    const a = reg.getOrCreate("guid-1").?;
    const b = reg.getOrCreate("guid-1").?;
    try std.testing.expectEqual(a, b);

    const c = reg.getOrCreate("guid-2").?;
    try std.testing.expect(a != c);
}

test "registry: a subscriber registered through one lookup sees a publisher's raise through another" {
    var reg = GameEventRegistry.init();
    const Ctx = struct {
        fired: bool = false,
        fn onRaised(self: *@This()) void {
            self.fired = true;
        }
    };
    var ctx = Ctx{};
    reg.getOrCreate("door-opened").?.on(&ctx, Ctx.onRaised);

    // A different call site resolving the SAME GUID reaches the same channel.
    reg.getOrCreate("door-opened").?.raise();
    try std.testing.expect(ctx.fired);
}
