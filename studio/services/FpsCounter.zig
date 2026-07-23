//! Persistent status-bar FPS counter. Counts every wakeup regardless of
//! trigger, so the rolling average reflects the actual redraw rate even when
//! the loop goes idle between input events.
const std = @import("std");

var frames_since_sample: u32 = 0;
var last_sample_ns: i128 = 0;
var rolling_fps: f32 = 0;

/// Call once per Studio main-loop iteration, with the same monotonic
/// timestamp `win.beginWait` returned. Recomputes the rolling average every
/// `sample_interval_ms`.
pub fn tick(nstime: i128, sample_interval_ms: i64) void {
    frames_since_sample += 1;
    if (last_sample_ns == 0) {
        last_sample_ns = nstime;
        return;
    }
    const elapsed_ns = nstime - last_sample_ns;
    const interval_ns: i128 = @as(i128, @intCast(@max(1, sample_interval_ms))) * std.time.ns_per_ms;
    if (elapsed_ns < interval_ns) return;

    rolling_fps = @floatCast(@as(f64, @floatFromInt(frames_since_sample)) * 1_000_000_000.0 / @as(f64, @floatFromInt(elapsed_ns)));
    frames_since_sample = 0;
    last_sample_ns = nstime;
}

/// The current rolling-average FPS, updated every `sample_interval_ms`.
pub fn fps() f32 {
    return rolling_fps;
}

test "tick computes a rolling average over the sample window" {
    frames_since_sample = 0;
    last_sample_ns = 0;
    rolling_fps = 0;

    tick(0, 500);
    try std.testing.expectEqual(@as(f32, 0), fps());

    // 30 frames over 500ms → 60 fps.
    var i: usize = 0;
    while (i < 29) : (i += 1) tick(0, 500);
    tick(500 * std.time.ns_per_ms, 500);
    try std.testing.expectApproxEqAbs(@as(f32, 60.0), fps(), 0.1);
}

test "tick does not update before the sample interval elapses" {
    frames_since_sample = 0;
    last_sample_ns = 0;
    rolling_fps = 5;

    tick(0, 500);
    tick(100 * std.time.ns_per_ms, 500);
    try std.testing.expectEqual(@as(f32, 5), fps());
}
