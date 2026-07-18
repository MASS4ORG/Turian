/// CHANGELOG.md entry generation for the release tool.
const std = @import("std");
const Proc = @import("Proc.zig");
const conv = @import("Conventional.zig");

/// Prepend a new versioned entry (grouped by conventional-commit section) to
/// CHANGELOG.md, creating the file when absent.
pub fn update(io: std.Io, gpa: std.mem.Allocator, version: []const u8, commits: []const []const u8) !void {
    var entry: std.ArrayList(u8) = .empty;
    defer entry.deinit(gpa);

    const date = currentDateStr(io);
    const header = try std.fmt.allocPrint(gpa, "## [{s}] - {s}\n", .{ version, &date });
    defer gpa.free(header);
    try entry.appendSlice(gpa, header);

    {
        var items: std.ArrayList([]const u8) = .empty;
        defer items.deinit(gpa);
        for (commits) |c| {
            if (conv.isBreaking(c)) try items.append(gpa, c);
        }
        if (items.items.len > 0) {
            try writeSection(gpa, &entry, "Breaking Changes", items.items);
        }
    }

    for (conv.sections) |sec| {
        var items: std.ArrayList([]const u8) = .empty;
        defer items.deinit(gpa);
        for (commits) |c| {
            if (conv.matchesKeywords(c, sec.keywords)) try items.append(gpa, c);
        }
        if (items.items.len > 0) {
            try writeSection(gpa, &entry, sec.label, items.items);
        }
    }
    try entry.append(gpa, '\n');

    const existing = Proc.readFile(io, gpa, "CHANGELOG.md") catch "";
    defer if (existing.len > 0) gpa.free(existing);

    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(gpa);

    if (std.mem.startsWith(u8, existing, "# Changelog")) {
        const nl2 = std.mem.indexOf(u8, existing, "\n\n") orelse existing.len;
        try out.appendSlice(gpa, existing[0 .. nl2 + 2]);
        try out.appendSlice(gpa, entry.items);
        if (nl2 + 2 < existing.len) try out.appendSlice(gpa, existing[nl2 + 2 ..]);
    } else {
        try out.appendSlice(gpa, "# Changelog\n\n");
        try out.appendSlice(gpa, entry.items);
        if (existing.len > 0) try out.appendSlice(gpa, existing);
    }

    try Proc.writeCwd(io, "CHANGELOG.md", out.items);
}

fn writeSection(gpa: std.mem.Allocator, entry: *std.ArrayList(u8), label: []const u8, items: []const []const u8) !void {
    const sec_header = try std.fmt.allocPrint(gpa, "\n### {s}\n", .{label});
    defer gpa.free(sec_header);
    try entry.appendSlice(gpa, sec_header);
    for (items) |it| {
        const line = try std.fmt.allocPrint(gpa, "- {s}\n", .{it});
        defer gpa.free(line);
        try entry.appendSlice(gpa, line);
    }
}

fn currentDateStr(io: std.Io) [10]u8 {
    const now = std.Io.Clock.now(.real, io);
    const ts: u64 = @intCast(@divTrunc(now.nanoseconds, std.time.ns_per_s));
    const epoch_s = std.time.epoch.EpochSeconds{ .secs = ts };
    const yd = epoch_s.getEpochDay().calculateYearDay();
    const md = yd.calculateMonthDay();
    var buf: [10]u8 = undefined;
    _ = std.fmt.bufPrint(&buf, "{d:0>4}-{d:0>2}-{d:0>2}", .{
        yd.year, md.month.numeric(), md.day_index + 1,
    }) catch {};
    return buf;
}
