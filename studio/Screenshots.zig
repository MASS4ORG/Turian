//! Viewport screenshot capture for the Studio.
//!
//! Captures the editor 3D viewport (the shared `render` module's color target,
//! i.e. the actual game scene as rendered by the GPU renderer) to a **PNG** file.
//! Useful for visual debugging, regression checks, and for both the user and an
//! LLM to inspect rendering glitches, proportions, and behaviour. (PNG, not TGA,
//! so every image viewer — and the LLM — can open it.)
//!
//! Files land in `<project>/screenshots/shot_NNNN.png` (or `./screenshots/` when
//! no project is open). Paths are reported project-relative — never absolute
//! (per project convention).

const std = @import("std");
const gui = @import("gui");
const GpuRenderer = @import("GpuRenderer.zig");
const EditorState = @import("EditorState.zig");

const page = std.heap.page_allocator;

/// Result of the last capture, for transient UI feedback in the profiler panel.
pub const Result = struct {
    ok: bool = false,
    /// Project-relative path of the written file (or an error note).
    path_buf: [256]u8 = undefined,
    path_len: usize = 0,

    pub fn path(self: *const Result) []const u8 {
        return self.path_buf[0..self.path_len];
    }
};

var g_last: Result = .{};

/// The most recent capture result (for showing "saved screenshots/shot_3.tga").
pub fn last() *const Result {
    return &g_last;
}

fn setLast(ok: bool, p: []const u8) void {
    g_last.ok = ok;
    const n = @min(p.len, g_last.path_buf.len);
    @memcpy(g_last.path_buf[0..n], p[0..n]);
    g_last.path_len = n;
}

fn fileExists(full: []const u8) bool {
    std.Io.Dir.cwd().access(gui.io, full, .{}) catch return false;
    return true;
}

/// Finds the next free `<project>/screenshots/shot_NNNN.png` path (so
/// sessions never clobber prior shots). Returns the full and project-relative
/// paths written into the given buffers, or null on failure (also updates
/// `last()`).
fn nextShotPath(fullbuf: []u8, relbuf: []u8) ?struct { full: []const u8, rel: []const u8 } {
    const io = gui.io;
    const dir = EditorState.project_path orelse ".";

    var dirbuf: [1024]u8 = undefined;
    const shots_dir = std.fmt.bufPrint(&dirbuf, "{s}/screenshots", .{dir}) catch {
        setLast(false, "path too long");
        return null;
    };
    std.Io.Dir.cwd().createDirPath(io, shots_dir) catch |err| {
        std.debug.print("[Screenshots] cannot create {s}: {any}\n", .{ shots_dir, err });
        setLast(false, "mkdir failed");
        return null;
    };

    var n: u32 = 1;
    return while (n < 100_000) : (n += 1) {
        const full = std.fmt.bufPrint(fullbuf, "{s}/shot_{d:0>4}.png", .{ shots_dir, n }) catch {
            setLast(false, "path too long");
            return null;
        };
        if (!fileExists(full)) {
            const rel = std.fmt.bufPrint(relbuf, "screenshots/shot_{d:0>4}.png", .{n}) catch full;
            break .{ .full = full, .rel = rel };
        }
    } else {
        setLast(false, "too many screenshots");
        return null;
    };
}

/// Encodes `pixels` (RGBA8, `w`x`h`) as PNG and writes it to the next free
/// `screenshots/shot_NNNN.png`. Returns the project-relative path on success,
/// null on failure. Both outcomes update `last()` for UI feedback. Shared by
/// `capture()` (bare 3D viewport) and `captureWindow()` (the whole window).
fn writeShot(pixels: []u8, w: u32, h: u32) ?[]const u8 {
    var fullbuf: [1280]u8 = undefined;
    var relbuf: [256]u8 = undefined;
    const paths = nextShotPath(&fullbuf, &relbuf) orelse return null;

    var out: std.Io.Writer.Allocating = std.Io.Writer.Allocating.initCapacity(page, 64 * 1024) catch {
        setLast(false, "out of memory");
        return null;
    };
    defer out.deinit();
    // writeWithResolution, not write: the latter calls dvui.windowNaturalScale(),
    // which needs a live dvui.current_window — gone by the time captureWindow's
    // caller (Main.zig, after win.end()) reaches this point.
    gui.PNGEncoder.writeWithResolution(&out.writer, pixels, w, h, 96) catch |err| {
        std.debug.print("[Screenshots] PNG encode failed: {any}\n", .{err});
        setLast(false, "encode failed");
        return null;
    };
    std.Io.Dir.cwd().writeFile(gui.io, .{ .sub_path = paths.full, .data = out.written() }) catch |err| {
        std.debug.print("[Screenshots] write {s} failed: {any}\n", .{ paths.rel, err });
        setLast(false, "write failed");
        return null;
    };
    std.debug.print("[Screenshots] saved {s}\n", .{paths.rel});
    setLast(true, paths.rel);
    return g_last.path();
}

/// Capture the 3D viewport (bare render target, no gizmos/UI/panels — see
/// `captureWindow` for the whole composited window) to the next free
/// `screenshots/shot_NNNN.png`. Returns the project-relative path on success,
/// null on failure. Both outcomes update `last()` for UI feedback.
pub fn capture() ?[]const u8 {
    const cap = GpuRenderer.capturePixels(page) orelse {
        setLast(false, "capture failed");
        return null;
    };
    defer page.free(cap.pixels);
    return writeShot(cap.pixels, cap.w, cap.h);
}

/// Capture the ENTIRE composited Studio window — menu bar, panels, inspector,
/// the 3D viewport, gizmos, icons, and any in-game GUI overlay — to the next
/// free `screenshots/shot_NNNN.png`. Unlike `capture()`, which only grabs the
/// bare 3D-viewport render target (drawn before dvui composites anything on
/// top), this sees exactly what a user would see on screen.
///
/// Must be driven from the main loop around a single frame's
/// `win.begin`/`win.end` — see `Main.zig`'s env-var-triggered capture — since
/// it needs to redirect that whole frame's render output through
/// `SDLBackend.beginFrameCapture`/`endFrameCapture`.
pub fn captureWindow(pixels: []u8, w: u32, h: u32) ?[]const u8 {
    return writeShot(pixels, w, h);
}
