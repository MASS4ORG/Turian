//! Editor-CPU frame phase timing (events/build/render/total), replacing the
//! dvui fork's `Window.frameTiming()`. Turian is back on upstream dvui (M0 of
//! the in-game GUI epic); `endRendering` is already public upstream, so the
//! same four-field breakdown is reproduced with `nanoTime` brackets around
//! Studio's own call sites in `Main.zig` instead of a dvui patch. The caller
//! supplies each timestamp (from `backend.nanoTime()`, the same clock dvui
//! itself uses internally) so this module stays clock-source-agnostic.
//!
//! Usage (see Main.zig's main loop):
//!   beginFrame(now)                  -- right before win.begin()
//!   markEventsEnd(now)               -- right after backend.addAllEvents()
//!   markBuildEnd(now)                -- right before win.endRendering(.{})
//!   endFrame(now)                    -- right after win.end(.{})
//! `last()` returns the most recently completed frame's timing.

const std = @import("std");

pub const FrameTiming = struct {
    events_ns: u64 = 0,
    build_ns: u64 = 0,
    render_ns: u64 = 0,
    total_ns: u64 = 0,
};

var last_timing: FrameTiming = .{};
var t_frame_start: i128 = 0;
var t_events_end: i128 = 0;
var t_build_end: i128 = 0;

fn nsSince(a: i128, b: i128) u64 {
    return @intCast(@max(0, a - b));
}

pub fn beginFrame(now: i128) void {
    t_frame_start = now;
}

pub fn markEventsEnd(now: i128) void {
    t_events_end = now;
}

pub fn markBuildEnd(now: i128) void {
    t_build_end = now;
}

pub fn endFrame(now: i128) void {
    last_timing = .{
        .events_ns = nsSince(t_events_end, t_frame_start),
        .build_ns = nsSince(t_build_end, t_events_end),
        .render_ns = nsSince(now, t_build_end),
        .total_ns = nsSince(now, t_frame_start),
    };
}

pub fn last() FrameTiming {
    return last_timing;
}

test "phase brackets compute events/build/render/total from marks" {
    const base: i128 = 1_000_000_000;
    beginFrame(base);
    markEventsEnd(base + 1_000_000); // 1ms events phase
    markBuildEnd(base + 3_000_000); // 2ms build phase
    endFrame(base + 6_000_000); // 3ms render phase

    const ft = last();
    try std.testing.expectEqual(@as(u64, 1_000_000), ft.events_ns);
    try std.testing.expectEqual(@as(u64, 2_000_000), ft.build_ns);
    try std.testing.expectEqual(@as(u64, 3_000_000), ft.render_ns);
    try std.testing.expectEqual(@as(u64, 6_000_000), ft.total_ns);
}

test "out-of-order marks clamp to zero instead of underflowing" {
    beginFrame(1000);
    markEventsEnd(500); // clock went backwards
    markBuildEnd(500);
    endFrame(500);

    const ft = last();
    try std.testing.expectEqual(@as(u64, 0), ft.events_ns);
    try std.testing.expectEqual(@as(u64, 0), ft.total_ns);
}
