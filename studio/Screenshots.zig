//! Viewport screenshot capture for the Studio (issue #35).
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

/// Capture the viewport to the next free `screenshots/shot_NNNN.tga`. Returns
/// the project-relative path on success, null on failure. Both outcomes update
/// `last()` for UI feedback.
pub fn capture() ?[]const u8 {
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

    // Find the first free index so sessions never clobber prior shots.
    var fullbuf: [1280]u8 = undefined;
    var relbuf: [256]u8 = undefined;
    var n: u32 = 1;
    const full, const rel = while (n < 100_000) : (n += 1) {
        const full = std.fmt.bufPrint(&fullbuf, "{s}/shot_{d:0>4}.png", .{ shots_dir, n }) catch {
            setLast(false, "path too long");
            return null;
        };
        if (!fileExists(full)) {
            const rel = std.fmt.bufPrint(&relbuf, "screenshots/shot_{d:0>4}.png", .{n}) catch full;
            break .{ full, rel };
        }
    } else {
        setLast(false, "too many screenshots");
        return null;
    };

    // Download the viewport pixels, then encode PNG (via dvui's stb-backed
    // encoder) into a growable buffer and write the file in one shot.
    const cap = GpuRenderer.capturePixels(page) orelse {
        setLast(false, "capture failed");
        return null;
    };
    defer page.free(cap.pixels);

    var out: std.Io.Writer.Allocating = std.Io.Writer.Allocating.initCapacity(page, 64 * 1024) catch {
        setLast(false, "out of memory");
        return null;
    };
    defer out.deinit();
    gui.PNGEncoder.write(&out.writer, cap.pixels, cap.w, cap.h) catch |err| {
        std.debug.print("[Screenshots] PNG encode failed: {any}\n", .{err});
        setLast(false, "encode failed");
        return null;
    };
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = full, .data = out.written() }) catch |err| {
        std.debug.print("[Screenshots] write {s} failed: {any}\n", .{ rel, err });
        setLast(false, "write failed");
        return null;
    };
    std.debug.print("[Screenshots] saved {s}\n", .{rel});
    setLast(true, rel);
    return g_last.path();
}
