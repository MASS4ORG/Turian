//! Diagnostic log ring buffer.
//!
//! Captures every `std.log` message (debug/info/warn/err) into a fixed ring so
//! external tools (the Remote Debug Protocol's `errors` method, the MCP
//! `list_errors` tool, the Studio Output panel) can read what happened
//! without scraping stderr. Install `logFn` as the process `std_options.logFn`
//! to feed it; it still forwards everything to the default logger.
//!
//! Consecutive identical (level, scope, message) records collapse into one
//! entry with an incrementing `repeat` count, matching Unity/Unreal/Godot
//! console behavior. `.err` entries additionally capture the call-site stack
//! trace's return addresses (cheap — no symbolization) so a viewer can
//! symbolize it lazily via `symbolizeTrace`.
//!
//! Logging can happen on any thread, so the ring is guarded by a tiny atomic
//! spinlock (no `Io` handle is available inside a `logFn`).

const std = @import("std");
const builtin = @import("builtin");

/// Severity captured by the ring, ascending by severity.
pub const Level = enum(u2) { debug, info, warn, err };

const MSG_CAP = 240;
const SCOPE_CAP = 32;
const TRACE_CAP = 12;
/// Ring capacity, exposed so callers (e.g. the Studio Output panel) can size
/// their own snapshot buffers to match.
pub const capacity = 512;

pub const Entry = struct {
    level: Level = .info,
    seq: u64 = 0,
    /// Wall-clock time this entry was last written (`record()` call, UTC), in
    /// milliseconds since the Unix epoch.
    timestamp_ms: i64 = 0,
    /// How many consecutive identical messages this entry collapses.
    repeat: u32 = 1,
    scope_buf: [SCOPE_CAP]u8 = undefined,
    scope_len: u8 = 0,
    msg_buf: [MSG_CAP]u8 = undefined,
    msg_len: u16 = 0,
    /// Raw return addresses captured at the `.err` call site (unsymbolized).
    trace_addrs: [TRACE_CAP]usize = @splat(0),
    trace_len: u8 = 0,

    pub fn scope(self: *const Entry) []const u8 {
        return self.scope_buf[0..self.scope_len];
    }
    pub fn message(self: *const Entry) []const u8 {
        return self.msg_buf[0..self.msg_len];
    }

    /// UTC time-of-day this entry was last written, for display (e.g. the
    /// Studio Output panel).
    pub fn timeOfDay(self: *const Entry) std.time.epoch.DaySeconds {
        const secs: u64 = @intCast(@divTrunc(self.timestamp_ms, 1000));
        return (std.time.epoch.EpochSeconds{ .secs = secs }).getDaySeconds();
    }
};

var g_ring: [capacity]Entry = undefined;
var g_head: usize = 0; // next write slot
var g_count: usize = 0; // entries written (capped at capacity)
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

/// Returns wall-clock time in milliseconds since Unix epoch.
/// - Uses raw OS bindings, not `std.Io.Timestamp`.
/// - Can be called before an `Io` instance exists, from any thread, at any time.
///
/// - Linux: uses `clock_gettime` via `std.c`.
/// - Windows: does not rely on libc's `clock_gettime`; uses its own native API branch.
fn nowMs() i64 {
    if (builtin.os.tag == .windows) {
        const hns: i64 = std.os.windows.ntdll.RtlGetSystemTimePrecise();
        const epoch_ns: i96 = std.time.epoch.windows * std.time.ns_per_s;
        const ns: i96 = @as(i96, hns) * 100 + epoch_ns;
        return @intCast(@divTrunc(ns, std.time.ns_per_ms));
    }
    var ts: std.c.timespec = undefined;
    if (std.c.clock_gettime(.REALTIME, &ts) != 0) return 0;
    return @as(i64, ts.sec) * 1000 + @divTrunc(@as(i64, ts.nsec), 1_000_000);
}

/// Records one entry, or bumps `repeat` if it's identical to the last one.
/// `msg` is truncated to the buffer capacity. Captures a stack trace for
/// `.err` (the caller is presumed to be the actual error site — see
/// `recordRemote` when that isn't true).
pub fn record(level: Level, scope_name: []const u8, msg: []const u8) void {
    recordImpl(level, scope_name, msg, true);
}

/// Like `record`, but never captures a stack trace. For entries relayed from
/// elsewhere (Play mode's dlopen'd script library, via `PlayMode.zig`'s
/// per-frame drain into the studio's own ring) — a trace captured *here*
/// would point at the relaying call site, not the original error, and would
/// fail to symbolize besides (it belongs to a different loaded binary's
/// debug info).
pub fn recordRemote(level: Level, scope_name: []const u8, msg: []const u8) void {
    recordImpl(level, scope_name, msg, false);
}

fn recordImpl(level: Level, scope_name: []const u8, msg: []const u8, capture_trace: bool) void {
    var scope_buf: [SCOPE_CAP]u8 = undefined;
    const sl = @min(scope_name.len, SCOPE_CAP);
    @memcpy(scope_buf[0..sl], scope_name[0..sl]);
    const scope_slice = scope_buf[0..sl];

    var msg_buf: [MSG_CAP]u8 = undefined;
    const ml = @min(msg.len, MSG_CAP);
    @memcpy(msg_buf[0..ml], msg[0..ml]);
    const msg_slice = msg_buf[0..ml];

    lock();
    defer unlock();

    const now = nowMs();

    if (g_count > 0) {
        const last = &g_ring[(g_head + capacity - 1) % capacity];
        if (last.level == level and std.mem.eql(u8, last.scope(), scope_slice) and std.mem.eql(u8, last.message(), msg_slice)) {
            last.repeat += 1;
            last.seq = g_seq;
            last.timestamp_ms = now;
            g_seq += 1;
            return;
        }
    }

    const e = &g_ring[g_head];
    e.level = level;
    e.seq = g_seq;
    e.timestamp_ms = now;
    g_seq += 1;
    e.repeat = 1;
    @memcpy(e.scope_buf[0..sl], scope_slice);
    e.scope_len = @intCast(sl);
    @memcpy(e.msg_buf[0..ml], msg_slice);
    e.msg_len = @intCast(ml);
    e.trace_len = 0;
    if (capture_trace and level == .err) {
        const st = std.debug.captureCurrentStackTrace(.{}, &e.trace_addrs);
        e.trace_len = @intCast(st.return_addresses.len);
    }
    g_head = (g_head + 1) % capacity;
    if (g_count < capacity) g_count += 1;
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

/// Copies up to `buf.len` entries into `buf`, newest first. Returns how many
/// were written. For UI consumers (Studio Output panel) that want direct
/// struct access instead of JSON.
pub fn snapshot(buf: []Entry) usize {
    lock();
    defer unlock();
    const n = @min(g_count, buf.len);
    var i: usize = 0;
    while (i < n) : (i += 1) {
        buf[i] = g_ring[(g_head + capacity - 1 - i) % capacity];
    }
    return n;
}

/// Symbolizes `entry`'s captured stack trace into an owned, human-readable
/// string (one `file:line: in function` line per frame). Does real debug-info
/// lookups, so only call this on demand (e.g. when a user expands an error
/// entry in the Output panel), not on every `record()`.
pub fn symbolizeTrace(alloc: std.mem.Allocator, entry: *const Entry) ![]u8 {
    if (entry.trace_len == 0) return alloc.dupe(u8, "(no stack trace captured)");
    var out: std.Io.Writer.Allocating = .init(alloc);
    defer out.deinit();
    // `StackTrace.return_addresses` wants a mutable slice; copy out of the
    // (borrowed, const) entry rather than widen `entry`'s own type.
    var addrs: [TRACE_CAP]usize = undefined;
    @memcpy(addrs[0..entry.trace_len], entry.trace_addrs[0..entry.trace_len]);
    const st: std.debug.StackTrace = .{
        .return_addresses = addrs[0..entry.trace_len],
        .skipped = .none,
    };
    const t: std.Io.Terminal = .{ .writer = &out.writer, .mode = .no_color };
    std.debug.writeStackTrace(&st, t) catch {};
    return alloc.dupe(u8, out.written());
}

/// Writes the captured entries as a JSON array, newest first, keeping only
/// entries at least as severe as `min_level`:
/// `[{ "level", "scope", "message", "seq", "repeat" }, ...]`.
pub fn writeJson(jw: *std.json.Stringify, min_level: Level) !void {
    lock();
    defer unlock();
    try jw.beginArray();
    var i: usize = 0;
    while (i < g_count) : (i += 1) {
        const idx = (g_head + capacity - 1 - i) % capacity;
        const e = &g_ring[idx];
        if (@intFromEnum(e.level) < @intFromEnum(min_level)) continue;
        try jw.beginObject();
        try jw.objectField("level");
        try jw.write(@tagName(e.level));
        try jw.objectField("scope");
        try jw.write(e.scope());
        try jw.objectField("message");
        try jw.write(e.message());
        try jw.objectField("seq");
        try jw.write(e.seq);
        try jw.objectField("repeat");
        try jw.write(e.repeat);
        try jw.endObject();
    }
    try jw.endArray();
}

/// Drop-in `std_options.logFn`: records every message into the ring, then
/// prints it to stderr — same shape as Zig's stock `std.log.defaultLog`
/// (colored by level, `scope(...)` prefix), plus a leading `HH:MM:SS`
/// timestamp so the terminal reads the same way the Output panel does.
pub fn logFn(
    comptime level: std.log.Level,
    comptime scope: @TypeOf(.enum_literal),
    comptime format: []const u8,
    args: anytype,
) void {
    var buf: [MSG_CAP]u8 = undefined;
    const msg = std.fmt.bufPrint(&buf, format, args) catch buf[0..buf.len];
    const lvl: Level = switch (level) {
        .err => .err,
        .warn => .warn,
        .info => .info,
        .debug => .debug,
    };
    record(lvl, @tagName(scope), msg);
    writeTerminal(level, scope, msg);
}

/// Prints one already-formatted message to stderr, timestamped and colored
/// by level — mirrors `std.log.defaultLogFileTerminal` but reuses the exact
/// (already-truncated) message text `record()` stored, and prepends a
/// wall-clock timestamp.
fn writeTerminal(comptime level: std.log.Level, comptime scope: @TypeOf(.enum_literal), msg: []const u8) void {
    var buffer: [128]u8 = undefined;
    const t = std.debug.lockStderr(&buffer).terminal();
    defer std.debug.unlockStderr();
    writeTerminalTimestamped(level, scope, msg, t) catch {};
}

fn writeTerminalTimestamped(
    comptime level: std.log.Level,
    comptime scope: @TypeOf(.enum_literal),
    msg: []const u8,
    t: std.Io.Terminal,
) std.Io.Writer.Error!void {
    const secs: u64 = @intCast(@divTrunc(nowMs(), 1000));
    const tod = (std.time.epoch.EpochSeconds{ .secs = secs }).getDaySeconds();

    t.setColor(.dim) catch {};
    try t.writer.print("{d:0>2}:{d:0>2}:{d:0>2} ", .{ tod.getHoursIntoDay(), tod.getMinutesIntoHour(), tod.getSecondsIntoMinute() });
    t.setColor(.reset) catch {};

    t.setColor(switch (level) {
        .err => .red,
        .warn => .yellow,
        .info => .green,
        .debug => .magenta,
    }) catch {};
    t.setColor(.bold) catch {};
    try t.writer.writeAll(level.asText());
    t.setColor(.reset) catch {};
    t.setColor(.dim) catch {};
    t.setColor(.bold) catch {};
    if (scope != .default) try t.writer.print("({t})", .{scope});
    try t.writer.writeAll(": ");
    t.setColor(.reset) catch {};
    try t.writer.print("{s}\n", .{msg});
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
    try writeJson(&jw, .debug);
    const json = out.written();
    // Newest ("second") appears before "first".
    const i_second = std.mem.indexOf(u8, json, "second").?;
    const i_first = std.mem.indexOf(u8, json, "first").?;
    try std.testing.expect(i_second < i_first);

    // Overflow wraps: write more than capacity, oldest dropped.
    reset();
    for (0..capacity + 10) |n| {
        var b: [16]u8 = undefined;
        record(.warn, "s", std.fmt.bufPrint(&b, "m{d}", .{n}) catch "m");
    }
    try std.testing.expectEqual(@as(usize, capacity), count());
}

test "writeJson filters by min_level" {
    reset();
    record(.debug, "s", "d");
    record(.info, "s", "i");
    record(.warn, "s", "w");
    record(.err, "s", "e");

    var out: std.Io.Writer.Allocating = .init(std.testing.allocator);
    defer out.deinit();
    var jw = std.json.Stringify{ .writer = &out.writer, .options = .{} };
    try writeJson(&jw, .warn);
    const json = out.written();
    try std.testing.expect(std.mem.indexOf(u8, json, "\"w\"") != null);
    try std.testing.expect(std.mem.indexOf(u8, json, "\"e\"") != null);
    try std.testing.expect(std.mem.indexOf(u8, json, "\"d\"") == null);
    try std.testing.expect(std.mem.indexOf(u8, json, "\"i\"") == null);
}

test "consecutive identical messages merge into repeat count" {
    reset();
    record(.info, "s", "hello");
    record(.info, "s", "hello");
    record(.info, "s", "hello");
    record(.info, "s", "different");
    try std.testing.expectEqual(@as(usize, 2), count());

    var buf: [2]Entry = undefined;
    const n = snapshot(&buf);
    try std.testing.expectEqual(@as(usize, 2), n);
    // Newest first: "different" (repeat 1), then "hello" (repeat 3).
    try std.testing.expectEqualStrings("different", buf[0].message());
    try std.testing.expectEqual(@as(u32, 1), buf[0].repeat);
    try std.testing.expectEqualStrings("hello", buf[1].message());
    try std.testing.expectEqual(@as(u32, 3), buf[1].repeat);
}

test "err level captures a non-empty stack trace" {
    reset();
    record(.err, "s", "boom");
    var buf: [1]Entry = undefined;
    _ = snapshot(&buf);
    if (buf[0].trace_len > 0) {
        const text = try symbolizeTrace(std.testing.allocator, &buf[0]);
        defer std.testing.allocator.free(text);
        try std.testing.expect(text.len > 0);
    }
}
