//! In-editor performance profiler (Guinevere/Turian,). A dockable panel
//! (#90 — see `Panels.zig`) with a Unity-style multi-track timeline, a
//! recordable history you can scrub, and Perfetto trace export.
//!
//! Data source: **`engine.Profiler`** — the *game-side* profiler. It keeps a
//! ring of recent frames; each holds per-thread CPU zones (`render.scene` & co.,
//! `scripts.update`) and render counters (draw calls, triangles, …) straight
//! from the GPU scene renderer. Recording is tied to Play mode and controllable
//! (Record/Pause + auto-on-Play). The `gui` (DVUI) frame timing is shown as a
//! secondary, collapsible "Editor CPU" readout.
//!
//! Toggle from View ▸ Show/Hide Profiler, or drag it around like any other
//! dock panel. Capture a screenshot or export a trace from the panel.

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const Screenshots = @import("../services/Screenshots.zig");
const ProfileExport = @import("ProfileExport.zig");
const GpuRenderer = @import("../scene-view/GpuRenderer.zig");
const PlayMode = @import("../scene-view/PlayMode.zig");
const EditorFrameTiming = @import("../services/EditorFrameTiming.zig");

const Frame = engine.Profiler.Frame;

// ── Recording control ────────────────────────────────────────────
/// Whether the profiler is actively recording (only effective during Play).
var g_record: bool = false;
/// Setting: start recording the moment Play mode is entered.
var g_auto_on_play: bool = true;
/// Edge-detect Play transitions to arm/disarm recording.
var g_prev_play: bool = false;

// ── History scrubbing ─────────────────────────────────────────────────────────
/// Absolute index of the frame pinned in the timeline; null = follow live.
var g_selected: ?u64 = null;

/// Collapsed state of the secondary editor-CPU section (collapsed by default so
/// the scene metrics stay front and centre).
var g_show_editor_cpu: bool = false;

/// True while the left button is held over the chart, so dragging scrubs the
/// pinned frame continuously (not just on click).
var g_chart_drag: bool = false;

/// Update recording state and arm `engine.Profiler` for this frame. MUST run
/// once per editor frame *before* `Profiler.beginFrame` (called from Window.zig).
/// Recording only happens during Play; Record/Pause and auto-on-Play decide
/// whether it's active.
pub fn tickRecording() void {
    const active = PlayMode.isActive();
    if (active and !g_prev_play) g_record = g_auto_on_play; // entered Play
    if (!active and g_prev_play) g_record = false; // left Play → freeze
    g_prev_play = active;
    engine.Profiler.enabled = active and g_record;
}

fn ms(ns: u64) f64 {
    return @as(f64, @floatFromInt(ns)) / 1_000_000.0;
}

/// One FPS-cap toggle button. `value` is the `Window.max_fps` it sets (null =
/// unlimited). Highlighted when it matches the active cap.
fn fpsBtn(cw: *gui.Window, label: []const u8, value: ?f32, id: usize) void {
    const active = std.meta.eql(cw.max_fps, value);
    if (gui.button(@src(), label, .{}, .{
        .id_extra = id,
        .gravity_y = 0.5,
        .style = if (active) .highlight else .control,
    })) {
        cw.max_fps = value;
    }
}

/// One history-length button; sets the profiler ring size (clears history).
fn histBtn(label: []const u8, frames: usize, id: usize) void {
    const active = engine.Profiler.history_limit == frames;
    if (gui.button(@src(), label, .{}, .{
        .id_extra = id,
        .gravity_y = 0.5,
        .style = if (active) .highlight else .control,
    })) {
        engine.Profiler.setHistoryLimit(frames);
    }
}

/// Distinct lane/zone colors, picked by name hash so a given scope keeps its
/// color across frames.
const palette = [_]gui.Color{
    .{ .r = 0x4f, .g = 0x8a, .b = 0xc4 }, // blue
    .{ .r = 0x6c, .g = 0xb0, .b = 0x4f }, // green
    .{ .r = 0xc4, .g = 0x8a, .b = 0x4f }, // orange
    .{ .r = 0xa0, .g = 0x6c, .b = 0xc4 }, // purple
    .{ .r = 0xc4, .g = 0x4f, .b = 0x6c }, // red
    .{ .r = 0x4f, .g = 0xc4, .b = 0xb0 }, // teal
    .{ .r = 0xbf, .g = 0xb0, .b = 0x4f }, // yellow
    .{ .r = 0x8a, .g = 0x8a, .b = 0x96 }, // gray
};

fn colorFor(name: []const u8) gui.Color {
    var h: u32 = 2166136261;
    for (name) |b| h = (h ^ b) *% 16777619;
    return palette[h % palette.len];
}

fn maxDepth(zones: []const engine.Profiler.Zone) u16 {
    var d: u16 = 0;
    for (zones) |*z| d = @max(d, z.depth);
    return d;
}

/// Resolve the frame to display: the pinned one if still in the ring, else live
/// (newest). Reverts the pin to live when the selected frame scrolls off.
fn displayFrame() *const Frame {
    if (g_selected) |sel| {
        const n = engine.Profiler.historyCount();
        var i: usize = 0;
        while (i < n) : (i += 1) {
            const f = engine.Profiler.frameAt(i);
            if (f.index == sel) return f;
        }
        g_selected = null; // scrolled out of history → resume live
    }
    return engine.Profiler.captured();
}

/// Panel content only (no window chrome) — for hosting inside `dvui.dockspace`.
/// Call once per editor frame while the panel is visible; the dockspace's own
/// walk only invokes this when the panel is actually part of the layout, so
/// there's no separate open/closed flag to check here.
pub fn drawContent() void {
    const cw = gui.currentWindow();

    // Request a frame every frame so the chart and timeline animate live even
    // when the editor is otherwise idle.
    gui.refresh(null, @src(), null);

    const frame = displayFrame();

    drawControls(cw);

    _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

    // --- frame-time chart (the game frame period; click to scrub history) ---
    drawTimeChart();

    _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

    // --- the timeline (the centerpiece — game CPU zones for the shown frame) ---
    drawTimeline(frame);

    _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

    // --- render counters (the game's GPU scene renderer) — front and centre ---
    const ec = frame.counters;
    gui.label(@src(), "Render (scene)", .{}, .{ .font = gui.Font.theme(.heading) });
    gui.label(@src(), "draw calls    {d}", .{ec.draw_calls}, .{});
    gui.label(@src(), "triangles     {d}", .{ec.triangles}, .{});
    gui.label(@src(), "vertices      {d}", .{ec.vertices}, .{});
    gui.label(@src(), "texture binds {d}", .{ec.texture_binds}, .{});
    gui.label(@src(), "mat switches  {d}", .{ec.material_switches}, .{});
    gui.label(@src(), "tex created   {d}", .{ec.textures_created}, .{});

    _ = gui.separator(@src(), .{ .expand = .horizontal, .margin = gui.Rect.all(4) });

    // --- editor window phases (secondary; collapsed by default) ---
    if (gui.expander(@src(), "Editor CPU (studio)", .{ .expanded = &g_show_editor_cpu }, .{ .expand = .horizontal })) {
        const ft = EditorFrameTiming.last();
        gui.label(@src(), "fps           {d:.0}", .{cw.FPS()}, .{});
        gui.label(@src(), "frame total   {d:.3} ms", .{ms(ft.total_ns)}, .{});
        gui.label(@src(), "  events {d:.3}", .{ms(ft.events_ns)}, .{});
        gui.label(@src(), "  build  {d:.3}", .{ms(ft.build_ns)}, .{});
        gui.label(@src(), "  render {d:.3}", .{ms(ft.render_ns)}, .{});
    }
}

/// Recording transport, view controls, vsync/fps, screenshot, export.
fn drawControls(cw: *gui.Window) void {
    const active = PlayMode.isActive();

    // status line
    {
        const green = gui.Color{ .r = 0x6c, .g = 0xb0, .b = 0x4f };
        const amber = gui.Color{ .r = 0xc0, .g = 0xa0, .b = 0x50 };
        var col: gui.Color = amber;
        var txt: []const u8 = "■ idle — press Play to profile";
        if (active and g_record) {
            col = green;
            txt = "● recording (Play)";
        } else if (active) {
            txt = "❚❚ paused (Play)";
        }
        gui.label(@src(), "{s}", .{txt}, .{ .color_text = col });
    }

    // transport: Record/Pause, Live, auto-on-Play
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal });
        defer row.deinit();
        if (gui.button(@src(), if (g_record) "❚❚ Pause" else "● Record", .{}, .{
            .gravity_y = 0.5,
            .style = if (g_record) .highlight else .control,
        })) {
            g_record = !g_record;
        }
        if (gui.button(@src(), if (g_selected == null) "● live" else "→ go live", .{}, .{
            .gravity_y = 0.5,
            .id_extra = 1,
            .style = if (g_selected == null) .highlight else .control,
        })) {
            g_selected = null;
        }
        _ = gui.checkbox(@src(), &g_auto_on_play, "auto-record on Play", .{ .gravity_y = 0.5 });
    }

    // vsync (independent) + fps cap
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal });
        defer row.deinit();
        gui.label(@src(), "present", .{}, .{ .gravity_y = 0.5, .padding = .{ .w = 6 } });
        const von = GpuRenderer.vsyncOn();
        if (gui.button(@src(), if (von) "vsync: on" else "vsync: off", .{}, .{
            .gravity_y = 0.5,
            .style = if (von) .highlight else .control,
        })) {
            GpuRenderer.requestVsync(!von);
        }
    }
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal });
        defer row.deinit();
        gui.label(@src(), "fps cap", .{}, .{ .gravity_y = 0.5, .padding = .{ .w = 6 } });
        fpsBtn(cw, "unlimited", null, 0);
        fpsBtn(cw, "30", 30, 1);
        fpsBtn(cw, "60", 60, 2);
        fpsBtn(cw, "90", 90, 3);
        fpsBtn(cw, "120", 120, 4);
    }

    // history length: how many recent frames to keep for scrubbing (changing it
    // clears the ring). Labelled in seconds assuming ~60 fps.
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal });
        defer row.deinit();
        gui.label(@src(), "history", .{}, .{ .gravity_y = 0.5, .padding = .{ .w = 6 } });
        histBtn("2s", 128, 0);
        histBtn("4s", 256, 1);
        histBtn("8s", 512, 2);
    }

    // screenshot + export
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal });
        defer row.deinit();
        if (gui.button(@src(), "Capture Screenshot", .{}, .{ .gravity_y = 0.5 })) {
            _ = Screenshots.capture();
        }
        if (gui.button(@src(), "Export Trace", .{}, .{ .gravity_y = 0.5, .id_extra = 1 })) {
            _ = ProfileExport.exportTrace();
        }
    }
    feedbackLabel(Screenshots.last());
    feedbackLabel(ProfileExport.last());
}

fn feedbackLabel(res: anytype) void {
    if (res.path_len == 0) return;
    const col: gui.Color = if (res.ok) .{ .r = 0x6c, .g = 0xb0, .b = 0x4f } else .{ .r = 0xc4, .g = 0x4f, .b = 0x6c };
    const prefix = if (res.ok) "saved " else "error: ";
    gui.label(@src(), "{s}{s}", .{ prefix, res.path() }, .{ .color_text = col });
}

/// Map a chart x (physical px) to a history position and pin that frame; the
/// newest column resumes live. No-op with no history.
fn scrubToX(px: f32, area: gui.Rect.Physical, count: usize) void {
    if (count == 0) return;
    const rel = std.math.clamp((px - area.x) / area.w, 0, 0.99999);
    const fi: usize = @intFromFloat(rel * @as(f32, @floatFromInt(count)));
    g_selected = if (fi >= count - 1) null else engine.Profiler.frameAt(fi).index;
}

/// Frame-time chart over the history ring, one stacked bar per pixel column (so
/// it's gapless at any width) on a fixed 0–50 ms scale with 60/30 fps reference
/// lines. Each bar's total height is the frame *period* (so a lower fps is a
/// taller bar), split into the **CPU busy** part (bottom, coloured by how it
/// compares to the 60/30 fps budget) and the **idle / cap-wait** part (top, dim
/// grey) — so capping the fps makes a tall mostly-grey bar, not a red one. Click
/// or drag to scrub the pinned frame; the newest column (or "go live") resumes.
fn drawTimeChart() void {
    const count = engine.Profiler.historyCount();
    const shown = displayFrame();
    const period_ms = ms(shown.period_ns);
    const busy_ms = ms(shown.total_ns);
    const fps: f64 = if (shown.period_ns > 0) 1e9 / @as(f64, @floatFromInt(shown.period_ns)) else 0;
    var span_s: f64 = 0;
    {
        var i: usize = 0;
        while (i < count) : (i += 1) span_s += ms(engine.Profiler.frameAt(i).period_ns);
        span_s /= 1000.0;
    }

    if (g_selected == null) {
        gui.label(@src(), "frame — {d:.0} fps  ·  {d:.2} ms CPU / {d:.2} ms total  ·  {d:.1}s history", .{ fps, busy_ms, period_ms, span_s }, .{ .font = gui.Font.theme(.heading) });
    } else {
        gui.label(@src(), "frame #{d} pinned — {d:.0} fps  ·  {d:.2} ms CPU / {d:.2} ms total", .{ shown.index, fps, busy_ms, period_ms }, .{ .font = gui.Font.theme(.heading) });
    }

    var box = gui.box(@src(), .{}, .{
        .expand = .horizontal,
        .min_size_content = .{ .w = 0, .h = 64 },
        .background = true,
        .style = .content,
        .margin = .{ .y = 4, .h = 4 },
    });
    defer box.deinit();

    const rs = box.data().rectScale();
    const area = rs.r;
    const scale = rs.s;
    if (area.w <= 0 or area.h <= 0 or count == 0) return;

    // Fixed 0..50 ms scale so steady-state fps differences are visible.
    const top: f64 = 50.0;
    const yFor = struct {
        fn f(a: gui.Rect.Physical, t: f64, v: f64) f32 {
            return a.y + a.h * @as(f32, @floatCast(1.0 - @min(v / t, 1.0)));
        }
    }.f;
    const bottom = area.y + area.h;

    // 60 fps (16.7 ms) and 30 fps (33.3 ms) reference lines.
    (gui.Rect.Physical{ .x = area.x, .y = yFor(area, top, 16.667), .w = area.w, .h = @max(1, scale) }).fill(.{}, .{ .color = .{ .r = 0x40, .g = 0x50, .b = 0x40 } });
    (gui.Rect.Physical{ .x = area.x, .y = yFor(area, top, 33.333), .w = area.w, .h = @max(1, scale) }).fill(.{}, .{ .color = .{ .r = 0x55, .g = 0x45, .b = 0x35 } });

    // Selected frame's history position (for the marker); newest if live.
    var sel_pos: usize = count - 1;
    if (g_selected) |sel| {
        var i: usize = 0;
        while (i < count) : (i += 1) if (engine.Profiler.frameAt(i).index == sel) {
            sel_pos = i;
        };
    }

    const idle_col = gui.Color{ .r = 0x3a, .g = 0x3a, .b = 0x44 }; // cap/vsync wait
    const cols: usize = @intFromFloat(@max(1, area.w));
    for (0..cols) |c| {
        const f = engine.Profiler.frameAt(c * count / cols);
        const period = ms(f.period_ns);
        if (period <= 0) continue;
        const busy = @min(ms(f.total_ns), period);
        const x = area.x + @as(f32, @floatFromInt(c));
        const w = @max(1, scale);

        // CPU busy segment (bottom), coloured by the budget it fits in.
        const y_busy = yFor(area, top, busy);
        const busy_col: gui.Color = if (busy <= 16.667) .{ .r = 0x4f, .g = 0x8a, .b = 0xc4 } // fits 60 fps
            else if (busy <= 33.333) .{ .r = 0xbf, .g = 0xb0, .b = 0x4f } // fits 30 fps
            else .{ .r = 0xc4, .g = 0x4f, .b = 0x4f }; // CPU-bound
        (gui.Rect.Physical{ .x = x, .y = y_busy, .w = w, .h = bottom - y_busy }).fill(.{}, .{ .color = busy_col });

        // Idle / cap-wait segment stacked on top, up to the full period.
        const y_period = yFor(area, top, period);
        if (y_busy - y_period >= 1)
            (gui.Rect.Physical{ .x = x, .y = y_period, .w = w, .h = y_busy - y_period }).fill(.{}, .{ .color = idle_col });
    }

    // Marker over the pinned/live frame.
    const mx = area.x + (@as(f32, @floatFromInt(sel_pos)) + 0.5) / @as(f32, @floatFromInt(count)) * area.w;
    (gui.Rect.Physical{ .x = mx, .y = area.y, .w = @max(1, scale), .h = area.h }).fill(.{}, .{ .color = .{ .r = 0xff, .g = 0xff, .b = 0xff } });

    // Click or drag to scrub: hold the left button and move to sweep the pin.
    for (gui.events()) |*e| {
        if (e.evt != .mouse) continue;
        const me = e.evt.mouse;
        switch (me.action) {
            .press => if (me.button == .left and area.contains(me.p)) {
                g_chart_drag = true;
                e.handle(@src(), box.data());
                scrubToX(me.p.x, area, count);
            },
            .motion, .position => if (g_chart_drag) {
                e.handle(@src(), box.data());
                scrubToX(me.p.x, area, count);
            },
            .release => if (me.button == .left and g_chart_drag) {
                g_chart_drag = false;
                e.handle(@src(), box.data());
            },
            else => {},
        }
    }
}

/// Draw the multi-track timeline for `frame`. Each thread is a lane; nested
/// zones stack downward by depth, laid out across the lane in proportion to the
/// frame duration (a flame graph).
fn drawTimeline(frame: *const Frame) void {
    const threads = frame.threadSlice();

    gui.label(@src(), "Timeline — frame #{d}  ({d:.2} ms CPU)", .{ frame.index, ms(frame.total_ns) }, .{
        .font = gui.Font.theme(.heading),
    });

    if (threads.len == 0 or frame.total_ns == 0) {
        gui.label(@src(), "(press Play and Record to capture a frame)", .{}, .{ .color_text = .{ .r = 0x88, .g = 0x88, .b = 0x88 } });
        return;
    }

    const span: f64 = @floatFromInt(frame.total_ns);
    const bar_font = gui.Font.theme(.body).withSize(11);

    for (threads, 0..) |*t, ti| {
        var lane = gui.box(@src(), .{ .dir = .vertical }, .{ .expand = .horizontal, .id_extra = ti });
        defer lane.deinit();

        gui.label(@src(), "{s}  ({d} zones)", .{ t.name(), t.zone_count }, .{
            .font = bar_font,
            .color_text = .{ .r = 0xb0, .g = 0xb0, .b = 0xc0 },
        });

        const rows: f32 = @floatFromInt(maxDepth(t.slice()) + 1);
        const row_h: f32 = 16;
        var bars = gui.box(@src(), .{}, .{
            .id_extra = ti,
            .expand = .horizontal,
            .min_size_content = .{ .w = 0, .h = rows * row_h },
            .background = true,
            .style = .content,
            .margin = .{ .y = 1, .h = 4 },
        });
        defer bars.deinit();

        const rs = bars.data().rectScale();
        const area = rs.r; // physical rect of this lane's bar strip
        const scale = rs.s;
        const rh = row_h * scale;

        for (t.slice()) |*z| {
            const dur: f64 = @floatFromInt(z.durationNs());
            const x0: f32 = @floatCast(@as(f64, @floatFromInt(z.start_ns)) / span);
            const wfrac: f32 = @floatCast(dur / span);
            var bar = gui.Rect.Physical{
                .x = area.x + x0 * area.w,
                .y = area.y + @as(f32, @floatFromInt(z.depth)) * rh,
                .w = @max(1, wfrac * area.w),
                .h = @max(1, rh - 1 * scale),
            };
            // Keep bars inside the strip.
            if (bar.x + bar.w > area.x + area.w) bar.w = area.x + area.w - bar.x;
            if (bar.w <= 0) continue;

            bar.fill(.{}, .{ .color = colorFor(z.name()) });

            // Label, clipped to the bar (skip on slivers too small for text).
            if (bar.w > 24 * scale) {
                const prev = gui.clip(bar);
                defer gui.clipSet(prev);
                const trs = gui.RectScale{
                    .r = .{ .x = bar.x + 3 * scale, .y = bar.y + 1 * scale, .w = bar.w, .h = bar.h },
                    .s = scale,
                };
                gui.renderText(.{
                    .font = bar_font,
                    .text = z.name(),
                    .rs = trs,
                    .color = .{ .r = 0x10, .g = 0x12, .b = 0x16 },
                }) catch {};
            }
        }
    }
}
