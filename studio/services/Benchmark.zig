//! Headless frame benchmark: renders a fixed number of frames from a fixed
//! camera, writes a Perfetto trace plus a counters summary, then quits. Frames
//! are driven from the main loop rather than by input, since an unfocused
//! window receives none, so the result is a lower bound on interactive cost.
//!
//! `--benchmark --scene <path> [--camera <name>] [--frames N] [--warmup N]
//! [--fps-cap N] [--uncapped]`. Runs are paced unless `--uncapped`, which holds
//! the GPU at full load for the whole run and should only be used on a machine
//! nobody is working on.
const std = @import("std");
const engine = @import("engine");
const EditorState = @import("editor").EditorState;

const log = std.log.scoped(.benchmark);
const Vector3 = engine.Vector3;

/// What `--benchmark` was asked to do. When `enabled` is false every entry
/// point here is a no-op.
pub const Config = struct {
    enabled: bool = false,
    /// Project-relative scene to open, or empty for whatever the project restores.
    scene: []const u8 = "",
    /// Name of a Camera node whose pose the editor camera is pinned to.
    camera: []const u8 = "",
    frames: u32 = 120,
    warmup: u32 = 40,
    /// Disable vsync and drive frames back to back. Off by default: an uncapped
    /// run holds the GPU at full load for the whole benchmark, which is not
    /// something to do to a machine somebody is using.
    uncapped: bool = false,
    /// Frame cap applied when not `uncapped`. 0 leaves the display's own rate.
    fps_cap: f32 = 0,
};

const Phase = enum { warmup, recording, done };

var g_cfg: Config = .{};
var g_phase: Phase = .warmup;
var g_seen: u32 = 0;
/// Project root, where the output directory is placed.
var g_project_buf: [1024]u8 = undefined;
var g_project_len: usize = 0;

pub fn configure(cfg: Config, project_path: []const u8) void {
    g_cfg = cfg;
    g_phase = .warmup;
    g_seen = 0;
    g_project_len = @min(project_path.len, g_project_buf.len);
    @memcpy(g_project_buf[0..g_project_len], project_path[0..g_project_len]);
}

pub fn enabled() bool {
    return g_cfg.enabled;
}

/// True while the run still needs frames, so the main loop must not park in
/// `waitEventTimeout`. Only in `uncapped` mode; otherwise the loop paces itself
/// against vsync or `fps_cap` like any other session.
pub fn wantsFrames() bool {
    return g_cfg.enabled and g_cfg.uncapped and g_phase != .done;
}

/// True while a capped run still needs frames — enough to keep an unfocused
/// window redrawing, without spinning.
pub fn wantsPacedFrames() bool {
    return g_cfg.enabled and !g_cfg.uncapped and g_phase != .done;
}

pub fn config() Config {
    return g_cfg;
}

/// Reads one benchmark flag from `args`, leaving unparseable values at their
/// defaults rather than failing the launch. False if `arg` isn't one.
pub fn parseArg(arg: []const u8, args: anytype, cfg: *Config) bool {
    if (std.mem.eql(u8, arg, "--benchmark")) {
        cfg.enabled = true;
        return true;
    }
    if (std.mem.eql(u8, arg, "--frames")) {
        if (args.next()) |v| cfg.frames = std.fmt.parseInt(u32, v, 10) catch cfg.frames;
        return true;
    }
    if (std.mem.eql(u8, arg, "--warmup")) {
        if (args.next()) |v| cfg.warmup = std.fmt.parseInt(u32, v, 10) catch cfg.warmup;
        return true;
    }
    if (std.mem.eql(u8, arg, "--uncapped")) {
        cfg.uncapped = true;
        return true;
    }
    if (std.mem.eql(u8, arg, "--fps-cap")) {
        if (args.next()) |v| cfg.fps_cap = std.fmt.parseFloat(f32, v) catch cfg.fps_cap;
        return true;
    }
    return false;
}

/// A viewport camera pose, reported rather than applied so this module stays
/// free of scene-view dependencies.
pub const Pose = struct {
    pos: Vector3,
    /// Degrees about Y, matching the node's Euler rotation.
    yaw: f32,
    /// Degrees about X, matching the node's Euler rotation.
    pitch: f32,
    fov: f32,
};

/// The named `--camera` node's own transform, or null when it isn't in the scene.
pub fn cameraPose() ?Pose {
    if (!g_cfg.enabled or g_cfg.camera.len == 0) return null;
    for (EditorState.objects[0..EditorState.object_count]) |*obj| {
        if (!std.mem.eql(u8, obj.nameSlice(), g_cfg.camera)) continue;
        for (obj.components[0..obj.component_count]) |*comp| {
            if (comp.* != .camera) continue;
            const r = obj.transform.rotation;
            return .{
                .pos = obj.transform.position,
                .yaw = r.y,
                .pitch = r.x,
                .fov = comp.camera.fov,
            };
        }
    }
    return null;
}

/// Advances one frame, returning true when the run is complete and Studio
/// should quit. Call after `Profiler.endFrame` has captured the frame.
pub fn tick(io: std.Io) bool {
    if (!g_cfg.enabled or g_phase == .done) return false;

    g_seen += 1;
    switch (g_phase) {
        .warmup => {
            if (g_seen >= g_cfg.warmup) {
                g_phase = .recording;
                g_seen = 0;
                // Discards warmup frames, whose shader compiles and asset
                // uploads would skew the percentiles.
                engine.Profiler.setHistoryLimit(engine.Profiler.MAX_HISTORY);
                log.info("warmup done ({d} frames) — recording {d}", .{ g_cfg.warmup, g_cfg.frames });
            }
        },
        .recording => {
            if (g_seen >= @min(g_cfg.frames, engine.Profiler.MAX_HISTORY)) {
                g_phase = .done;
                writeResults(io) catch |err| log.err("writing results failed: {any}", .{err});
                return true;
            }
        },
        .done => {},
    }
    return false;
}

/// Frame periods in ms, sorted ascending, for the percentiles below.
fn collectPeriods(buf: []f64) []f64 {
    var n: usize = 0;
    for (0..engine.Profiler.historyCount()) |i| {
        if (n >= buf.len) break;
        const f = engine.Profiler.frameAt(i);
        // Zero marks the first recorded frame, not a real 0 ms one.
        if (f.period_ns == 0) continue;
        buf[n] = @as(f64, @floatFromInt(f.period_ns)) / 1_000_000.0;
        n += 1;
    }
    std.sort.pdq(f64, buf[0..n], {}, std.sort.asc(f64));
    return buf[0..n];
}

fn percentile(sorted: []const f64, p: f64) f64 {
    if (sorted.len == 0) return 0;
    const idx: usize = @intFromFloat(@min(
        @as(f64, @floatFromInt(sorted.len - 1)),
        @max(0.0, p * @as(f64, @floatFromInt(sorted.len - 1))),
    ));
    return sorted[idx];
}

/// `<project>/benchmark/<frames>f-<timestamp>/`, created on demand.
fn makeOutDir(io: std.Io, buf: []u8) ![]const u8 {
    const project = g_project_buf[0..g_project_len];
    const stamp = std.Io.Clock.boot.now(io).nanoseconds;
    const dir = try std.fmt.bufPrint(buf, "{s}/benchmark/{d}f-{d}", .{ project, g_cfg.frames, @as(u64, @intCast(@max(0, stamp))) });
    std.Io.Dir.cwd().createDirPath(io, dir) catch |err| switch (err) {
        error.PathAlreadyExists => {},
        else => return err,
    };
    return dir;
}

/// Total draw calls across the recorded frames. Zero means the viewport never
/// rendered, so the frame times measure an idle editor.
fn recordedDrawCalls() u64 {
    var total: u64 = 0;
    for (0..engine.Profiler.historyCount()) |i|
        total += engine.Profiler.frameAt(i).counters.draw_calls;
    return total;
}

fn writeResults(io: std.Io) !void {
    var dir_buf: [1200]u8 = undefined;
    const dir = try makeOutDir(io, &dir_buf);

    var period_buf: [engine.Profiler.MAX_HISTORY]f64 = undefined;
    const periods = collectPeriods(&period_buf);
    const draws = recordedDrawCalls();
    if (draws == 0)
        log.warn("no draw calls recorded — the scene viewport never rendered; these frame times measure an idle editor", .{});

    // ── summary.json ────────────────────────────────────────────────────────
    {
        var out: std.Io.Writer.Allocating = .init(std.heap.page_allocator);
        defer out.deinit();
        var jw = std.json.Stringify{ .writer = &out.writer, .options = .{ .whitespace = .indent_2 } };
        try jw.beginObject();
        try jw.objectField("scene");
        try jw.write(g_cfg.scene);
        try jw.objectField("camera");
        try jw.write(g_cfg.camera);
        try jw.objectField("uncapped");
        try jw.write(g_cfg.uncapped);
        try jw.objectField("frames_recorded");
        try jw.write(periods.len);
        try jw.objectField("total_draw_calls");
        try jw.write(draws);
        if (draws == 0) {
            try jw.objectField("warning");
            try jw.write("no draw calls recorded — the scene viewport never rendered; these frame times measure an idle editor, not the renderer");
        }
        try jw.objectField("frame_ms");
        try jw.beginObject();
        try jw.objectField("p50");
        try jw.write(percentile(periods, 0.50));
        try jw.objectField("p95");
        try jw.write(percentile(periods, 0.95));
        try jw.objectField("p99");
        try jw.write(percentile(periods, 0.99));
        try jw.objectField("min");
        try jw.write(if (periods.len > 0) periods[0] else 0);
        try jw.objectField("max");
        try jw.write(if (periods.len > 0) periods[periods.len - 1] else 0);
        try jw.endObject();
        try jw.objectField("last_frame");
        try engine.Profiler.writeFrameJson(&jw);
        try jw.endObject();

        var path_buf: [1300]u8 = undefined;
        const path = try std.fmt.bufPrint(&path_buf, "{s}/summary.json", .{dir});
        try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = out.written() });
        log.info("wrote {s}", .{path});
    }

    // ── trace.json (Perfetto / chrome://tracing) ────────────────────────────
    {
        var out: std.Io.Writer.Allocating = .init(std.heap.page_allocator);
        defer out.deinit();
        try engine.Profiler.writeChromeTrace(&out.writer);

        var path_buf: [1300]u8 = undefined;
        const path = try std.fmt.bufPrint(&path_buf, "{s}/trace.json", .{dir});
        try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = out.written() });
        log.info("wrote {s}", .{path});
    }

    log.info("benchmark: p50={d:.2}ms p95={d:.2}ms over {d} frames, {d} draw calls", .{
        percentile(periods, 0.50),
        percentile(periods, 0.95),
        periods.len,
        draws,
    });
    if (!g_cfg.uncapped)
        log.info("capped run: frame times reflect the present interval, not the work done — per-pass CPU zones are the usable numbers here. Use --uncapped for throughput.", .{});
}

// ── Tests ────────────────────────────────────────────────────────────────────

const testing = std.testing;

test "percentile indexes a sorted sample without going out of range" {
    const s = [_]f64{ 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
    try testing.expectEqual(@as(f64, 1), percentile(&s, 0.0));
    try testing.expectEqual(@as(f64, 10), percentile(&s, 1.0));
    try testing.expectEqual(@as(f64, 5), percentile(&s, 0.5));
    try testing.expectEqual(@as(f64, 0), percentile(&.{}, 0.5));
}

test "parseArg reads frame counts and leaves bad values at their default" {
    var it = std.mem.tokenizeScalar(u8, "600 notanumber", ' ');
    const Iter = struct {
        inner: *std.mem.TokenIterator(u8, .scalar),
        fn next(self: *@This()) ?[]const u8 {
            return self.inner.next();
        }
    };
    var iter = Iter{ .inner = &it };

    var cfg = Config{};
    try testing.expect(parseArg("--benchmark", &iter, &cfg));
    try testing.expect(cfg.enabled);
    try testing.expect(parseArg("--frames", &iter, &cfg));
    try testing.expectEqual(@as(u32, 600), cfg.frames);
    // Compared against the default itself, so changing it doesn't fail here.
    try testing.expect(parseArg("--warmup", &iter, &cfg));
    try testing.expectEqual((Config{}).warmup, cfg.warmup);
    try testing.expect(!parseArg("--project", &iter, &cfg));
}

test "a run is capped unless --uncapped is passed" {
    var it = std.mem.tokenizeScalar(u8, "30", ' ');
    const Iter = struct {
        inner: *std.mem.TokenIterator(u8, .scalar),
        fn next(self: *@This()) ?[]const u8 {
            return self.inner.next();
        }
    };
    var iter = Iter{ .inner = &it };

    var cfg = Config{};
    try testing.expect(!cfg.uncapped);
    try testing.expect(parseArg("--fps-cap", &iter, &cfg));
    try testing.expectEqual(@as(f32, 30), cfg.fps_cap);
    try testing.expect(!cfg.uncapped);
    try testing.expect(parseArg("--uncapped", &iter, &cfg));
    try testing.expect(cfg.uncapped);
}

test "only an uncapped run drives frames back to back" {
    g_cfg = .{ .enabled = true, .uncapped = false };
    g_phase = .warmup;
    try testing.expect(!wantsFrames());
    try testing.expect(wantsPacedFrames());

    g_cfg.uncapped = true;
    try testing.expect(wantsFrames());
    try testing.expect(!wantsPacedFrames());

    // A finished run asks for nothing either way.
    g_phase = .done;
    try testing.expect(!wantsFrames());
    g_cfg.uncapped = false;
    try testing.expect(!wantsPacedFrames());

    g_cfg = .{};
    g_phase = .warmup;
}
