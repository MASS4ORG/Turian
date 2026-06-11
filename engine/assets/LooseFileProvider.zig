//! Asset provider that reads loose files from a directory tree.
//!
//! Used during development so assets can be edited and reloaded without
//! repackaging. The asset key is interpreted as a path relative to `root`.

const std = @import("std");
const provider_api = @import("Provider.zig");
const Provider = provider_api.Provider;

const LooseFileProvider = @This();

/// Base directory; asset keys resolve relative to it. Borrowed, not owned.
root: []const u8,

pub fn init(root: []const u8) LooseFileProvider {
    return .{ .root = root };
}

pub fn provider(self: *LooseFileProvider) Provider {
    return .{ .ptr = self, .vtable = &vtable };
}

const vtable = Provider.VTable{ .read = readFn };

fn readFn(
    ptr: *anyopaque,
    gpa: std.mem.Allocator,
    io: std.Io,
    key: []const u8,
) anyerror![]u8 {
    const self: *LooseFileProvider = @ptrCast(@alignCast(ptr));
    var path_buf: [1024]u8 = undefined;
    const path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ self.root, key }) catch
        return provider_api.Error.AssetNotFound;
    return std.Io.Dir.cwd().readFileAlloc(io, path, gpa, .unlimited) catch
        return provider_api.Error.AssetNotFound;
}
