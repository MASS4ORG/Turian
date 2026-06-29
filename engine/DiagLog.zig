//! Diagnostic log ring buffer.
//!
//! Captures the most recent `std.log` `.warn`/`.err` messages into a fixed ring
//! so external tools (the Remote Debug Protocol's `errors` method, the MCP
//! `list_errors` tool) can read what recently went wrong without scraping
//! stderr. Install `logFn` as the process `std_options.logFn` to feed it; it
//! still forwards everything to the default logger.
//!
//! Logging can happen on any thread, so the ring is guarded by a tiny atomic
//! spinlock (no `Io` handle is available inside a `logFn`).

const std = @import("std");

/// Severity captured by the ring (info/debug are ignored).
pub const Level = enum { warn, err };

const MSG_CAP = 240;
const SCOPE_CAP = 32;
const RING_CAP = 128;

pub const Entry = struct {
    level: Level = .warn,
    seq: u64 = 0,
    scope_buf: [SCOPE_CAP]u8 = undefined,
    scope_len: u8 = 0,
    msg_buf: [MSG_CAP]u8 = undefined,
    msg_len: u16 = 0,

    pub fn scope(self: *const Entry) []const u8 {
        return self.scope_buf[0..self.scope_len];
    }
    pub fn message(self: *const Entry) []const u8 {
        return self.msg_buf[0..self.msg_len];
    }
};

var g_ring: [RING_CAP]Entry = undefined;
var g_head: usize = 0; // next write slot
var g_count: usize = 0; // entries written (capped at RING_CAP)
var g_seq: u64 = 0;
var g_lock: std.atomic.Value(u32) = .{ .raw = 0 };

fn lock() void {
    while (g_lock.cmpxchgWeak(0, 1, .acquire, .monotonic) != null) {
        std.atomic.spinLoopHint();
    }
}
fn unlock() void {
    g_lock.store(0, .release);
}

/// Records one entry. `msg` is truncated to the buffer capacity.
pub fn record(level: Level, scope_name: []const u8, msg: []const u8) void {
    lock();
    defer unlock();
    const e = &g_ring[g_head];
    e.level = level;
    e.seq = g_seq;
    g_seq += 1;
    const sl = @min(scope_name.len, SCOPE_CAP);
    @memcpy(e.scope_buf[0..sl], scope_name[0..sl]);
    e.scope_len = @intCast(sl);
    const ml = @min(msg.len, MSG_CAP);
    @memcpy(e.msg_buf[0..ml], msg[0..ml]);
    e.msg_len = @intCast(ml);
    g_head = (g_head + 1) % RING_CAP;
    if (g_count < RING_CAP) g_count += 1;
}

/// Clears the ring (mainly for tests).
pub fn reset() void {
    lock();
    defer unlock();
    g_head = 0;
    g_count = 0;
    g_seq = 0;
}

pub fn count() usize {
    lock();
    defer unlock();
    return g_count;
}

/// Writes the captured entries as a JSON array, newest first:
/// `[{ "level", "scope", "message", "seq" }, ...]`.
pub fn writeJson(jw: *std.json.Stringify) !void {
    lock();
    defer unlock();
    try jw.beginArray();
    var i: usize = 0;
    while (i < g_count) : (i += 1) {
        const idx = (g_head + RING_CAP - 1 - i) % RING_CAP;
        const e = &g_ring[idx];
        try jw.beginObject();
        try jw.objectField("level");
        try jw.write(@tagName(e.level));
        try jw.objectField("scope");
        try jw.write(e.scope());
        try jw.objectField("message");
        try jw.write(e.message());
        try jw.objectField("seq");
        try jw.write(e.seq);
        try jw.endObject();
    }
    try jw.endArray();
}

/// Drop-in `std_options.logFn`: records `.warn`/`.err` into the ring, then
/// forwards every message to Zig's default logger.
pub fn logFn(
    comptime level: std.log.Level,
    comptime scope: @TypeOf(.enum_literal),
    comptime format: []const u8,
    args: anytype,
) void {
    switch (level) {
        .warn, .err => {
            var buf: [MSG_CAP]u8 = undefined;
            const msg = std.fmt.bufPrint(&buf, format, args) catch buf[0..buf.len];
            record(if (level == .err) .err else .warn, @tagName(scope), msg);
        },
        else => {},
    }
    std.log.defaultLog(level, scope, format, args);
}

// ── Tests ────────────────────────────────────────────────────────────────────

test "ring captures newest-first and wraps" {
    reset();
    record(.warn, "test", "first");
    record(.err, "test", "second");
    try std.testing.expectEqual(@as(usize, 2), count());

    var out: std.Io.Writer.Allocating = .init(std.testing.allocator);
    defer out.deinit();
    var jw = std.json.Stringify{ .writer = &out.writer, .options = .{} };
    try writeJson(&jw);
    const json = out.written();
    // Newest ("second") appears before "first".
    const i_second = std.mem.indexOf(u8, json, "second").?;
    const i_first = std.mem.indexOf(u8, json, "first").?;
    try std.testing.expect(i_second < i_first);

    // Overflow wraps: write more than capacity, oldest dropped.
    reset();
    for (0..RING_CAP + 10) |n| {
        var b: [16]u8 = undefined;
        record(.warn, "s", std.fmt.bufPrint(&b, "m{d}", .{n}) catch "m");
    }
    try std.testing.expectEqual(@as(usize, RING_CAP), count());
}
