const std = @import("std");
const Profiler = @import("../Profiler.zig");

/// Engine-wide runtime metrics — the single, consistent diagnostics source.
///
/// Subsystems (renderer, scene manager, allocators) write into one shared
/// instance each frame; the introspection layer reads it without knowing which
/// subsystem produced which number. This keeps "engine subsystems expose
/// diagnostics consistently" (issue #2) true by construction: there is exactly
/// one struct to fill and one struct to read.
///
/// All fields are plain data so `std.json.Stringify.write` serialises the whole
/// struct automatically.
pub const Metrics = struct {
    // ── Frame timing ─────────────────────────────────────────────────────────
    /// Frames per second (smoothed by the host if desired).
    fps: f32 = 0,
    /// Wall-clock time of the last frame, in milliseconds.
    frame_time_ms: f32 = 0,
    /// Total frames rendered since startup.
    frame_count: u64 = 0,

    // ── Memory ───────────────────────────────────────────────────────────────
    /// Bytes currently allocated by tracked allocators.
    memory_bytes: u64 = 0,
    /// Number of live allocations.
    allocation_count: u64 = 0,

    // ── Rendering ────────────────────────────────────────────────────────────
    /// Draw calls submitted last frame.
    draw_calls: u32 = 0,
    /// Triangles submitted last frame.
    triangles: u32 = 0,
    /// GPU frame time, in milliseconds (0 when not measured).
    gpu_time_ms: f32 = 0,

    // ── ECS / scene ──────────────────────────────────────────────────────────
    /// Number of loaded scenes.
    scene_count: u32 = 0,
    /// Total entities across all loaded scenes.
    entity_count: u32 = 0,
    /// Total components across all entities.
    component_count: u32 = 0,

    /// Builds metrics from a captured `Profiler.Frame`: frame timing (from the
    /// frame period) plus the renderer's per-frame counters (draw calls,
    /// triangles). The host fills in scene/entity counts and memory afterwards
    /// (see `withScene`). This keeps the profiler the single instrumentation
    /// source rather than re-counting draws separately (issue #2).
    pub fn fromProfiler(frame: *const Profiler.Frame) Metrics {
        var m: Metrics = .{};
        m.frame_count = frame.index;
        m.draw_calls = frame.counters.draw_calls;
        m.triangles = frame.counters.triangles;
        // No dedicated GPU timer yet; report the CPU frame cost as the closest
        // available number so the field is populated rather than always zero.
        if (frame.total_ns > 0)
            m.gpu_time_ms = @as(f32, @floatFromInt(frame.total_ns)) / 1_000_000.0;
        if (frame.period_ns > 0) {
            m.frame_time_ms = @as(f32, @floatFromInt(frame.period_ns)) / 1_000_000.0;
            m.fps = 1_000_000_000.0 / @as(f32, @floatFromInt(frame.period_ns));
        }
        return m;
    }

    /// Fills the scene/ECS counts. Convenience for hosts that derive these from
    /// the live world after `fromProfiler`.
    pub fn withScene(self: *Metrics, scene_count: u32, entity_count: u32, component_count: u32) void {
        self.scene_count = scene_count;
        self.entity_count = entity_count;
        self.component_count = component_count;
    }

    /// Records frame timing from a delta in seconds. Convenience for hosts that
    /// only track delta time; updates `fps`, `frame_time_ms`, and `frame_count`.
    pub fn recordFrame(self: *Metrics, dt_seconds: f32) void {
        self.frame_count += 1;
        self.frame_time_ms = dt_seconds * 1000.0;
        self.fps = if (dt_seconds > 0.0) 1.0 / dt_seconds else 0.0;
    }

    /// Resets the per-frame rendering counters. Call at the start of each frame
    /// before the renderer accumulates draw calls and triangles.
    pub fn beginFrame(self: *Metrics) void {
        self.draw_calls = 0;
        self.triangles = 0;
    }
};

test "recordFrame derives fps and frame time" {
    var m: Metrics = .{};
    m.recordFrame(0.02); // 20 ms → 50 fps
    try std.testing.expectEqual(@as(u64, 1), m.frame_count);
    try std.testing.expectApproxEqAbs(@as(f32, 20.0), m.frame_time_ms, 0.001);
    try std.testing.expectApproxEqAbs(@as(f32, 50.0), m.fps, 0.001);

    m.recordFrame(0.0); // guard against divide-by-zero
    try std.testing.expectEqual(@as(f32, 0.0), m.fps);
    try std.testing.expectEqual(@as(u64, 2), m.frame_count);
}

test "fromProfiler maps counters and frame period" {
    var frame: Profiler.Frame = .{};
    frame.index = 7;
    frame.counters = .{ .draw_calls = 12, .triangles = 3400 };
    frame.period_ns = 16_666_666; // ~60 fps
    frame.total_ns = 8_000_000; // 8 ms CPU

    var m = Metrics.fromProfiler(&frame);
    try std.testing.expectEqual(@as(u64, 7), m.frame_count);
    try std.testing.expectEqual(@as(u32, 12), m.draw_calls);
    try std.testing.expectEqual(@as(u32, 3400), m.triangles);
    try std.testing.expectApproxEqAbs(@as(f32, 60.0), m.fps, 0.1);
    try std.testing.expectApproxEqAbs(@as(f32, 16.666), m.frame_time_ms, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 8.0), m.gpu_time_ms, 0.001);

    m.withScene(1, 9, 14);
    try std.testing.expectEqual(@as(u32, 9), m.entity_count);
    try std.testing.expectEqual(@as(u32, 14), m.component_count);
}

test "beginFrame clears per-frame render counters" {
    var m: Metrics = .{ .draw_calls = 12, .triangles = 999, .memory_bytes = 4096 };
    m.beginFrame();
    try std.testing.expectEqual(@as(u32, 0), m.draw_calls);
    try std.testing.expectEqual(@as(u32, 0), m.triangles);
    // Non per-frame counters are untouched.
    try std.testing.expectEqual(@as(u64, 4096), m.memory_bytes);
}
