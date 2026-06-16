//! Lightweight polling file watcher for the project's `assets/` tree.
//!
//! The asset browser used to expose a manual "Refresh" button. Instead we poll
//! the assets directory on a throttled interval and recompute a cheap signature
//! (per-file size + mtime, folded into a hash). When the signature changes we
//! report it so the caller can hot-reload via `EditorState.refreshComponents`.
//!
//! This is deliberately a poll rather than an OS-native watch (inotify/kqueue/
//! ReadDirectoryChangesW): it is fully cross-platform, needs no extra threads,
//! and a once-per-second walk of a project's assets tree is negligible.

const std = @import("std");
const EditorState = @import("EditorState.zig");

/// How often to re-scan the assets tree.
const POLL_INTERVAL_NS: i128 = 1 * std.time.ns_per_s;

var last_poll_ns: i128 = 0;
var last_sig: u64 = 0;
var has_baseline: bool = false;
/// Project path the current baseline belongs to (so switching projects
/// re-baselines instead of firing a spurious change).
var baseline_project_buf: [1024]u8 = undefined;
var baseline_project_len: usize = 0;

/// Forget the current baseline; the next `poll` records a fresh one without
/// reporting a change. Call when a project is opened or closed.
pub fn reset() void {
    has_baseline = false;
    baseline_project_len = 0;
    last_poll_ns = 0;
}

/// Poll the assets tree (throttled). Returns true when a change has been
/// detected since the last reported state, meaning the caller should refresh.
pub fn poll(io: std.Io, now_ns: i128) bool {
    const proj = EditorState.project_path orelse {
        if (has_baseline) reset();
        return false;
    };

    if (last_poll_ns != 0 and now_ns - last_poll_ns < POLL_INTERVAL_NS) return false;
    last_poll_ns = now_ns;

    var path_buf: [1024]u8 = undefined;
    const assets = std.fmt.bufPrint(&path_buf, "{s}/assets", .{proj}) catch return false;

    var sig: u64 = 1469598103934665603; // FNV-1a offset basis
    hashDir(io, assets, &sig);

    // Re-baseline (no change reported) when there is no baseline yet or the
    // project path differs from the one the baseline was taken for.
    const same_project = std.mem.eql(u8, baseline_project_buf[0..baseline_project_len], proj);
    if (!has_baseline or !same_project) {
        last_sig = sig;
        has_baseline = true;
        const n = @min(proj.len, baseline_project_buf.len);
        @memcpy(baseline_project_buf[0..n], proj[0..n]);
        baseline_project_len = n;
        return false;
    }

    if (sig != last_sig) {
        last_sig = sig;
        return true;
    }
    return false;
}

fn fold(sig: *u64, bytes: []const u8) void {
    for (bytes) |b| {
        sig.* ^= b;
        sig.* *%= 1099511628211; // FNV-1a prime
    }
}

fn hashDir(io: std.Io, dir_path: []const u8, sig: *u64) void {
    var dir = std.Io.Dir.cwd().openDir(io, dir_path, .{ .iterate = true }) catch return;
    defer dir.close(io);

    var iter = dir.iterate();
    while (iter.next(io) catch null) |entry| {
        var sub_buf: [1024]u8 = undefined;
        const sub_path = std.fmt.bufPrint(&sub_buf, "{s}/{s}", .{ dir_path, entry.name }) catch continue;
        if (entry.kind == .directory) {
            hashDir(io, sub_path, sig);
        } else if (entry.kind == .file) {
            fold(sig, entry.name);
            if (dir.statFile(io, entry.name, .{})) |st| {
                fold(sig, std.mem.asBytes(&st.size));
                const mtime: i96 = st.mtime.nanoseconds;
                fold(sig, std.mem.asBytes(&mtime));
            } else |_| {}
        }
    }
}
