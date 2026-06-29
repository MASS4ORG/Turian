//! Export captured profiler history to a Chrome/Perfetto trace file.
//!
//! Writes the engine profiler's frame ring as Chrome trace-event JSON, which
//! loads directly in <https://ui.perfetto.dev> or `chrome://tracing` for deep
//! analysis (per-zone durations, frame-over-frame comparison, flame charts).
//! This is the "export for analysis" path; importing back into the Studio isn't
//! needed — Perfetto is the analysis tool.
//!
//! Files land in `<project>/profiles/trace_NNNN.json` (or `./profiles/` when no
//! project is open). Paths are reported project-relative — never absolute.

const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const EditorState = @import("EditorState.zig");

const page = std.heap.page_allocator;

pub const Result = struct {
    ok: bool = false,
    path_buf: [256]u8 = undefined,
    path_len: usize = 0,

    pub fn path(self: *const Result) []const u8 {
        return self.path_buf[0..self.path_len];
    }
};

var g_last: Result = .{};

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

/// Export the current history ring to the next free `profiles/trace_NNNN.json`.
/// Returns the project-relative path on success, null on failure; both update
/// `last()` for UI feedback.
pub fn exportTrace() ?[]const u8 {
    const io = gui.io;
    if (engine.Profiler.historyCount() == 0) {
        setLast(false, "nothing recorded yet");
        return null;
    }

    const dir = EditorState.project_path orelse ".";
    var dirbuf: [1024]u8 = undefined;
    const out_dir = std.fmt.bufPrint(&dirbuf, "{s}/profiles", .{dir}) catch {
        setLast(false, "path too long");
        return null;
    };
    std.Io.Dir.cwd().createDirPath(io, out_dir) catch |err| {
        std.debug.print("[ProfileExport] cannot create {s}: {any}\n", .{ out_dir, err });
        setLast(false, "mkdir failed");
        return null;
    };

    var fullbuf: [1280]u8 = undefined;
    var relbuf: [256]u8 = undefined;
    var n: u32 = 1;
    const full, const rel = while (n < 100_000) : (n += 1) {
        const full = std.fmt.bufPrint(&fullbuf, "{s}/trace_{d:0>4}.json", .{ out_dir, n }) catch {
            setLast(false, "path too long");
            return null;
        };
        if (!fileExists(full)) {
            const rel = std.fmt.bufPrint(&relbuf, "profiles/trace_{d:0>4}.json", .{n}) catch full;
            break .{ full, rel };
        }
    } else {
        setLast(false, "too many traces");
        return null;
    };

    var out: std.Io.Writer.Allocating = std.Io.Writer.Allocating.initCapacity(page, 256 * 1024) catch {
        setLast(false, "out of memory");
        return null;
    };
    defer out.deinit();
    engine.Profiler.writeChromeTrace(&out.writer) catch |err| {
        std.debug.print("[ProfileExport] encode failed: {any}\n", .{err});
        setLast(false, "encode failed");
        return null;
    };
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = full, .data = out.written() }) catch |err| {
        std.debug.print("[ProfileExport] write {s} failed: {any}\n", .{ rel, err });
        setLast(false, "write failed");
        return null;
    };
    std.debug.print("[ProfileExport] saved {s} ({d} frames)\n", .{ rel, engine.Profiler.historyCount() });
    setLast(true, rel);
    return g_last.path();
}
