//! In-engine performance profiler: nested CPU zones and per-frame render
//! counters. Each thread keeps its own lock-free zone buffer; `endFrame`
//! snapshots everything into a `Frame` the UI reads.

const std = @import("std");
const builtin = @import("builtin");

/// Max concurrently-tracked threads (main + worker pools).
pub const MAX_THREADS = 8;
/// Recorded zones kept per thread per frame; excess zones in a frame are dropped
/// (depth tracking still stays balanced so timing never corrupts).
pub const MAX_ZONES_PER_THREAD = 96;
/// Max nesting depth of scopes on one thread.
pub const MAX_DEPTH = 64;
/// Fixed capacity for a zone / thread name (longer names are truncated).
pub const NAME_CAP = 48;
/// Hard cap on the frame-history ring (static storage; ~55 KB/frame). The
/// *effective* number kept is `history_limit`, adjustable at runtime so the UI
/// can trade memory for a longer scrub-back window. 512 ≈ 8.5 s @ 60 fps.
pub const MAX_HISTORY = 512;
/// Effective number of recent frames kept (≤ `MAX_HISTORY`). Change via
/// `setHistoryLimit` (it clears the ring). Default ≈ 4 s @ 60 fps.
pub var history_limit: usize = 256;

/// Runtime master switch. Defaults on in Debug, off otherwise so shipped games
/// pay nothing unless they opt in (`engine.Profiler.enabled = true`).
pub var enabled: bool = builtin.mode == .Debug;

/// Monotonic clock source. Set once via `setIo`; without it timestamps read 0
/// (zones still record, with zero duration — handy in tests).
var g_io: ?std.Io = null;

/// Provide the I/O handle backing the monotonic clock. Call once at startup.
pub fn setIo(io: std.Io) void {
    g_io = io;
}

fn nowNs() u64 {
    const io = g_io orelse return 0;
    const ts = std.Io.Clock.boot.now(io);
    return @intCast(@max(0, ts.nanoseconds));
}

// ── Data ────────────────────────────────────────────────────────────────────

/// One completed scope: a name, its nesting depth, and start/end nanoseconds
/// **relative to the frame start** (so the UI can lay it out on a timeline).
pub const Zone = struct {
    name_buf: [NAME_CAP]u8 = undefined,
    name_len: u8 = 0,
    depth: u16 = 0,
    start_ns: u64 = 0,
    end_ns: u64 = 0,

    pub fn name(self: *const Zone) []const u8 {
        return self.name_buf[0..self.name_len];
    }

    pub fn durationNs(self: *const Zone) u64 {
        return self.end_ns -| self.start_ns;
    }
};

/// Per-frame rendering cost counters, bumped by the renderer. Mirrors the
/// GUI-side `RenderStats` but for the game's GPU scene renderer.
pub const Counters = struct {
    draw_calls: u32 = 0,
    triangles: u32 = 0,
    vertices: u32 = 0,
    texture_binds: u32 = 0,
    textures_created: u32 = 0,
    material_switches: u32 = 0,
    submeshes_drawn: u32 = 0,
    submeshes_culled: u32 = 0,
};

/// One thread's recorded zones for a captured frame.
pub const ThreadFrame = struct {
    id: u64 = 0,
    name_buf: [NAME_CAP]u8 = undefined,
    name_len: u8 = 0,
    zones: [MAX_ZONES_PER_THREAD]Zone = undefined,
    zone_count: usize = 0,

    pub fn name(self: *const ThreadFrame) []const u8 {
        return self.name_buf[0..self.name_len];
    }

    pub fn slice(self: *const ThreadFrame) []const Zone {
        return self.zones[0..self.zone_count];
    }
};

/// A snapshot of one completed frame for the UI: every thread's zones plus the
/// counters and the frame's wall-clock duration.
pub const Frame = struct {
    index: u64 = 0,
    /// Frame start, in the same monotonic ns base as zone timestamps (absolute
    /// boot-clock ns — used as the cross-frame base for trace export).
    start_ns: u64 = 0,
    /// Wall-clock CPU time from `beginFrame` to `endFrame`.
    total_ns: u64 = 0,
    /// Wall-clock time between this frame's start and the previous frame's start
    /// — the actual frame period, so 1e9/period_ns is the on-screen fps. Rises
    /// when an fps cap or vsync limits the rate. 0 for the first recorded frame.
    period_ns: u64 = 0,
    counters: Counters = .{},
    threads: [MAX_THREADS]ThreadFrame = @splat(.{}),
    thread_count: usize = 0,

    pub fn threadSlice(self: *const Frame) []const ThreadFrame {
        return self.threads[0..self.thread_count];
    }
};

// ── Live recording state ─────────────────────────────────────────────────────

const StackEntry = struct {
    name: []const u8 = "",
    start_ns: u64 = 0,
    /// Recorded-zone index reserved at push time. Null when the per-thread
    /// zone buffer is full (zones beyond the limit are silently dropped).
    rec: ?usize = null,
};

/// Live per-thread recorder. Touched only by its owning thread on the hot path.
const Track = struct {
    id: u64 = 0,
    name_buf: [NAME_CAP]u8 = undefined,
    name_len: u8 = 0,
    stack: [MAX_DEPTH]StackEntry = undefined,
    depth: usize = 0,
    zones: [MAX_ZONES_PER_THREAD]Zone = undefined,
    zone_count: usize = 0,
};

var g_tracks: [MAX_THREADS]Track = undefined;
var g_track_count: usize = 0;

/// Tiny atomic spinlock guarding one-time per-thread track registration. Held
/// only on a thread's first zone; the hot path (push/pop) is lock-free since
/// each thread touches only its own track. Mirrors `editor.TaskManager`'s
/// spinlock — avoids threading an `Io` through `std.Io.Mutex`.
var g_reg_held: std.atomic.Value(bool) = .init(false);

fn regLock() void {
    while (g_reg_held.swap(true, .acquire)) std.atomic.spinLoopHint();
}
fn regUnlock() void {
    g_reg_held.store(false, .release);
}

/// Cached track index for the calling thread (-1 = not yet assigned).
threadlocal var tl_track: i32 = -1;

var g_frame_index: u64 = 0;
var g_frame_start_ns: u64 = 0;
var g_prev_start_ns: u64 = 0;
var g_period_ns: u64 = 0;
var g_capturing: bool = false;

/// Ring of recent captured frames: zones write into the live tracks; `endFrame`
/// copies into the ring head for the UI to read (chart + timeline scrubbing)
/// while the next frame records. `g_frame_head` is the next write slot. Only the
/// first `history_limit` slots are used.
var g_frames: [MAX_HISTORY]Frame = @splat(.{});
var g_frame_head: usize = 0;
var g_frame_count: usize = 0;
/// Returned when no frame has been captured yet.
var g_empty: Frame = .{};

/// Set how many recent frames to keep (clamped to `[16, MAX_HISTORY]`). Clears
/// the existing history.
pub fn setHistoryLimit(n: usize) void {
    history_limit = std.math.clamp(n, 16, MAX_HISTORY);
    g_frame_head = 0;
    g_frame_count = 0;
}

/// Find (or, on first call this run, allocate) the calling thread's track.
/// Returns null if the thread table is full.
fn track() ?*Track {
    if (tl_track >= 0) return &g_tracks[@intCast(tl_track)];

    regLock();
    defer regUnlock();
    if (g_track_count >= MAX_THREADS) return null;

    const idx = g_track_count;
    g_track_count += 1;
    var t = &g_tracks[idx];
    t.* = .{};
    t.id = currentThreadId();
    // Default name: "main" for the first registrant, "thread N" otherwise.
    if (idx == 0) {
        setName(&t.name_buf, &t.name_len, "main");
    } else {
        var buf: [NAME_CAP]u8 = undefined;
        const s = std.fmt.bufPrint(&buf, "thread {d}", .{idx}) catch "thread";
        setName(&t.name_buf, &t.name_len, s);
    }
    tl_track = @intCast(idx);
    return t;
}

fn currentThreadId() u64 {
    return @intCast(std.Thread.getCurrentId());
}

fn setName(buf: *[NAME_CAP]u8, len: *u8, s: []const u8) void {
    const n = @min(s.len, NAME_CAP);
    @memcpy(buf[0..n], s[0..n]);
    len.* = @intCast(n);
}

/// Name the calling thread's track (shown as the timeline lane label).
pub fn nameThread(s: []const u8) void {
    if (!enabled) return;
    const t = track() orelse return;
    setName(&t.name_buf, &t.name_len, s);
}

// ── Frame lifecycle ──────────────────────────────────────────────────────────

/// Begin a new frame: clear every thread's recorded zones and counters. Pair
/// with `endFrame`.
pub fn beginFrame() void {
    if (!enabled) return;
    g_capturing = true;
    g_frame_index +%= 1;
    const start = nowNs();
    // Frame period = gap since the previous frame's start. Clamp out long gaps
    // (recording was paused, or the first frame) so the chart isn't spiked.
    const gap = start -| g_prev_start_ns;
    g_period_ns = if (g_prev_start_ns != 0 and gap < std.time.ns_per_s) gap else 0;
    g_prev_start_ns = start;
    g_frame_start_ns = start;
    g_counters = .{};
    for (g_tracks[0..g_track_count]) |*t| {
        t.zone_count = 0;
        t.depth = 0;
    }
}

/// End the frame: snapshot all tracks + counters into the ring's head frame.
pub fn endFrame() void {
    if (!enabled or !g_capturing) return;
    g_capturing = false;
    const end_ns = nowNs();

    var dst = &g_frames[g_frame_head];
    dst.index = g_frame_index;
    dst.start_ns = g_frame_start_ns;
    dst.total_ns = end_ns -| g_frame_start_ns;
    dst.period_ns = g_period_ns;
    dst.counters = g_counters;
    dst.thread_count = g_track_count;

    for (g_tracks[0..g_track_count], 0..) |*src, i| {
        var td = &dst.threads[i];
        td.id = src.id;
        @memcpy(td.name_buf[0..src.name_len], src.name_buf[0..src.name_len]);
        td.name_len = src.name_len;
        const n = src.zone_count;
        td.zone_count = n;
        @memcpy(td.zones[0..n], src.zones[0..n]);
    }

    const cap = @max(1, history_limit);
    g_frame_head = (g_frame_head + 1) % cap;
    if (g_frame_count < cap) g_frame_count += 1;
}

/// The most recently completed frame snapshot (or an empty frame if none yet).
/// Stable until the next `endFrame`. Safe to read from the UI thread.
pub fn captured() *const Frame {
    if (g_frame_count == 0) return &g_empty;
    const cap = @max(1, history_limit);
    return &g_frames[(g_frame_head + cap - 1) % cap];
}

/// Number of frames currently held in the history ring (0..history_limit).
pub fn historyCount() usize {
    return g_frame_count;
}

/// Captured frame at history position `i`, where `0` is the oldest still held
/// and `historyCount()-1` is the newest. Returns the empty frame if out of range.
pub fn frameAt(i: usize) *const Frame {
    if (i >= g_frame_count) return &g_empty;
    const cap = @max(1, history_limit);
    const oldest = (g_frame_head + cap - g_frame_count) % cap;
    return &g_frames[(oldest + i) % cap];
}

// ── Export ───────────────────────────────────────────────────────────────────

/// Serialises the most recently captured frame as structured JSON for the
/// Remote Debug Protocol's `profiler.capture`: frame timing, the
/// render counters, and each thread's zones (name + duration in microseconds).
pub fn writeFrameJson(jw: *std.json.Stringify) !void {
    const f = captured();
    try jw.beginObject();
    try jw.objectField("frame_index");
    try jw.write(f.index);
    try jw.objectField("total_ms");
    try jw.write(@as(f64, @floatFromInt(f.total_ns)) / 1_000_000.0);
    try jw.objectField("period_ms");
    try jw.write(@as(f64, @floatFromInt(f.period_ns)) / 1_000_000.0);
    try jw.objectField("counters");
    try jw.write(f.counters);
    try jw.objectField("threads");
    try jw.beginArray();
    for (f.threadSlice()) |*t| {
        try jw.beginObject();
        try jw.objectField("name");
        try jw.write(t.name());
        try jw.objectField("zones");
        try jw.beginArray();
        for (t.slice()) |*z| {
            try jw.beginObject();
            try jw.objectField("name");
            try jw.write(z.name());
            try jw.objectField("duration_us");
            try jw.write(@as(f64, @floatFromInt(z.durationNs())) / 1000.0);
            try jw.endObject();
        }
        try jw.endArray();
        try jw.endObject();
    }
    try jw.endArray();
    try jw.endObject();
}

fn writeJsonEscaped(w: *std.Io.Writer, s: []const u8) !void {
    for (s) |ch| switch (ch) {
        '"' => try w.writeAll("\\\""),
        '\\' => try w.writeAll("\\\\"),
        else => try w.writeByte(ch),
    };
}

/// Write the whole history ring as a Chrome/Perfetto **trace event JSON**
/// (`{"traceEvents":[...]}`) — load it directly in <https://ui.perfetto.dev> or
/// `chrome://tracing` to analyse zones across frames. Each zone becomes a
/// complete ("X") event with µs `ts`/`dur`; threads are one Perfetto track each
///.
pub fn writeChromeTrace(w: *std.Io.Writer) !void {
    try w.writeAll("{\"traceEvents\":[");
    var first = true;

    // Thread-name metadata from the newest frame (stable thread ids = tids).
    if (g_frame_count > 0) {
        for (frameAt(g_frame_count - 1).threadSlice()) |*t| {
            if (!first) try w.writeByte(',');
            first = false;
            try w.print("{{\"ph\":\"M\",\"name\":\"thread_name\",\"pid\":1,\"tid\":{d},\"args\":{{\"name\":\"", .{t.id});
            try writeJsonEscaped(w, t.name());
            try w.writeAll("\"}}");
        }
    }

    var i: usize = 0;
    while (i < g_frame_count) : (i += 1) {
        const f = frameAt(i);
        for (f.threadSlice()) |*t| {
            for (t.slice()) |*z| {
                if (!first) try w.writeByte(',');
                first = false;
                const ts_us = @as(f64, @floatFromInt(f.start_ns + z.start_ns)) / 1000.0;
                const dur_us = @as(f64, @floatFromInt(z.durationNs())) / 1000.0;
                try w.writeAll("{\"ph\":\"X\",\"name\":\"");
                try writeJsonEscaped(w, z.name());
                try w.print("\",\"pid\":1,\"tid\":{d},\"ts\":{d:.3},\"dur\":{d:.3}}}", .{ t.id, ts_us, dur_us });
            }
        }
    }

    try w.writeAll("]}");
}

// ── Zones ────────────────────────────────────────────────────────────────────

/// RAII scope handle. `defer scope.end()` closes the zone.
pub const Scope = struct {
    active: bool = false,

    pub fn end(self: *Scope) void {
        if (self.active) {
            endZone();
            self.active = false;
        }
    }
};

/// Open a scoped zone: `var z = Profiler.zone("name"); defer z.end();`
pub fn zone(name: []const u8) Scope {
    if (!enabled or !g_capturing) return .{};
    beginZone(name);
    return .{ .active = true };
}

/// Manually open a zone. Must be balanced by `endZone` on the same thread.
/// Prefer `zone(...)` + `defer end()`.
pub fn beginZone(name: []const u8) void {
    if (!enabled or !g_capturing) return;
    const t = track() orelse return;
    if (t.depth >= MAX_DEPTH) {
        t.depth += 1; // keep balance; nothing recorded past the limit
        return;
    }

    const start = nowNs();
    var rec: ?usize = null;
    if (t.zone_count < MAX_ZONES_PER_THREAD) {
        const i = t.zone_count;
        t.zone_count += 1;
        var z = &t.zones[i];
        setName(&z.name_buf, &z.name_len, name);
        z.depth = @intCast(t.depth);
        z.start_ns = start -| g_frame_start_ns;
        z.end_ns = z.start_ns;
        rec = i;
    }
    t.stack[t.depth] = .{ .name = name, .start_ns = start, .rec = rec };
    t.depth += 1;
}

/// Close the innermost open zone on the calling thread.
pub fn endZone() void {
    if (!enabled or !g_capturing) return;
    const t = track() orelse return;
    if (t.depth == 0) return;
    t.depth -= 1;
    if (t.depth >= MAX_DEPTH) return; // was an over-depth push; nothing recorded
    const e = t.stack[t.depth];
    if (e.rec) |i| {
        t.zones[i].end_ns = nowNs() -| g_frame_start_ns;
    }
}

// ── Counters ─────────────────────────────────────────────────────────────────

var g_counters: Counters = .{};

/// Record one draw call and its primitive load. `textured` marks draws that
/// bound a texture (for the texture-bind counter).
pub fn countDraw(triangles: u32, vertices: u32, textured: bool) void {
    if (!enabled or !g_capturing) return;
    g_counters.draw_calls += 1;
    g_counters.triangles += triangles;
    g_counters.vertices += vertices;
    if (textured) g_counters.texture_binds += 1;
}

/// Record a GPU material/pipeline switch.
pub fn countMaterialSwitch() void {
    if (!enabled or !g_capturing) return;
    g_counters.material_switches += 1;
}

/// Record `n` newly created GPU textures this frame.
pub fn countTexturesCreated(n: u32) void {
    if (!enabled or !g_capturing) return;
    g_counters.textures_created += n;
}

/// Record `n` submeshes that passed frustum culling and were drawn.
pub fn countSubmeshesDrawn(n: u32) void {
    if (!enabled or !g_capturing) return;
    g_counters.submeshes_drawn += n;
}

/// Record `n` submeshes skipped by frustum culling (outside the camera view).
pub fn countSubmeshesCulled(n: u32) void {
    if (!enabled or !g_capturing) return;
    g_counters.submeshes_culled += n;
}

/// Live counters for the in-progress frame (the captured snapshot lags by one
/// `endFrame`). Handy for an overlay HUD that wants the current numbers.
pub fn liveCounters() Counters {
    return g_counters;
}

// ── Tests ────────────────────────────────────────────────────────────────────

const testing = std.testing;

/// Reset all global state so tests are independent.
fn resetForTest() void {
    g_track_count = 0;
    g_frame_index = 0;
    g_frame_start_ns = 0;
    g_prev_start_ns = 0;
    g_period_ns = 0;
    g_capturing = false;
    g_counters = .{};
    g_frame_head = 0;
    g_frame_count = 0;
    g_empty = .{};
    history_limit = 256;
    tl_track = -1;
    g_io = null;
}

test "scoped zones nest with correct depth and are captured per frame" {
    enabled = true;
    resetForTest();
    defer resetForTest();

    beginFrame();
    {
        var outer = zone("outer");
        defer outer.end();
        {
            var inner = zone("inner");
            defer inner.end();
        }
        {
            var sib = zone("sibling");
            defer sib.end();
        }
    }
    endFrame();

    const f = captured();
    try testing.expectEqual(@as(usize, 1), f.thread_count);
    const zones = f.threads[0].slice();
    try testing.expectEqual(@as(usize, 3), zones.len);
    // Recorded in push order: outer(0), inner(1), sibling(1).
    try testing.expectEqualStrings("outer", zones[0].name());
    try testing.expectEqual(@as(u16, 0), zones[0].depth);
    try testing.expectEqualStrings("inner", zones[1].name());
    try testing.expectEqual(@as(u16, 1), zones[1].depth);
    try testing.expectEqualStrings("sibling", zones[2].name());
    try testing.expectEqual(@as(u16, 1), zones[2].depth);
}

test "history ring keeps recent frames addressable oldest..newest" {
    enabled = true;
    resetForTest();
    defer resetForTest();
    setHistoryLimit(16);
    const cap = history_limit;

    // Record more frames than the ring holds; only the last `cap` survive.
    const total = cap + 5;
    var k: usize = 0;
    while (k < total) : (k += 1) {
        beginFrame();
        var z = zone("f");
        z.end();
        endFrame();
    }

    try testing.expectEqual(cap, historyCount());
    // Frame indices are monotonic; newest == total, oldest == total-cap+1.
    try testing.expectEqual(@as(u64, total), captured().index);
    try testing.expectEqual(@as(u64, total), frameAt(cap - 1).index);
    try testing.expectEqual(@as(u64, total - cap + 1), frameAt(0).index);
}

test "chrome trace export is well-formed JSON with zone events" {
    enabled = true;
    resetForTest();
    defer resetForTest();

    beginFrame();
    {
        var z = zone("render.scene");
        defer z.end();
    }
    endFrame();

    var out: std.Io.Writer.Allocating = .init(testing.allocator);
    defer out.deinit();
    try writeChromeTrace(&out.writer);
    const json = out.written();

    try testing.expect(std.mem.startsWith(u8, json, "{\"traceEvents\":["));
    try testing.expect(std.mem.endsWith(u8, json, "]}"));
    try testing.expect(std.mem.indexOf(u8, json, "\"name\":\"render.scene\"") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"ph\":\"X\"") != null);
    // Parses as valid JSON.
    const parsed = try std.json.parseFromSlice(std.json.Value, testing.allocator, json, .{});
    defer parsed.deinit();
}

test "counters accumulate within a frame and reset across frames" {
    enabled = true;
    resetForTest();
    defer resetForTest();

    beginFrame();
    countDraw(2, 6, true);
    countDraw(4, 12, false);
    countMaterialSwitch();
    countTexturesCreated(3);
    countSubmeshesDrawn(2);
    countSubmeshesCulled(5);
    endFrame();

    var c = captured().counters;
    try testing.expectEqual(@as(u32, 2), c.draw_calls);
    try testing.expectEqual(@as(u32, 6), c.triangles);
    try testing.expectEqual(@as(u32, 18), c.vertices);
    try testing.expectEqual(@as(u32, 1), c.texture_binds);
    try testing.expectEqual(@as(u32, 1), c.material_switches);
    try testing.expectEqual(@as(u32, 3), c.textures_created);
    try testing.expectEqual(@as(u32, 2), c.submeshes_drawn);
    try testing.expectEqual(@as(u32, 5), c.submeshes_culled);

    beginFrame();
    countDraw(1, 3, false);
    endFrame();
    c = captured().counters;
    try testing.expectEqual(@as(u32, 1), c.draw_calls);
    try testing.expectEqual(@as(u32, 3), c.vertices);
}

test "disabled profiler records nothing" {
    enabled = false;
    resetForTest();
    defer {
        resetForTest();
        enabled = builtin.mode == .Debug;
    }

    beginFrame();
    {
        var z = zone("ignored");
        defer z.end();
        countDraw(10, 30, true);
    }
    endFrame();

    const f = captured();
    try testing.expectEqual(@as(usize, 0), f.thread_count);
    try testing.expectEqual(@as(u32, 0), f.counters.draw_calls);
}

test "over-deep nesting stays balanced and does not corrupt timing" {
    enabled = true;
    resetForTest();
    defer resetForTest();

    beginFrame();
    var i: usize = 0;
    while (i < MAX_DEPTH + 10) : (i += 1) beginZone("deep");
    i = 0;
    while (i < MAX_DEPTH + 10) : (i += 1) endZone();
    // One legal zone after the storm must still record cleanly at depth 0.
    {
        var z = zone("after");
        defer z.end();
    }
    endFrame();

    const t = &captured().threads[0];
    try testing.expectEqual(@as(usize, 0), t.zones[t.zone_count - 1].depth);
    try testing.expectEqualStrings("after", t.zones[t.zone_count - 1].name());
}
