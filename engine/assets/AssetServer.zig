//! Mounts an ordered stack of asset providers and resolves keys against them.
//!
//! Providers are queried from most-recently to least-recently mounted, so a
//! later mount overrides an earlier one for any shared key. That is exactly the
//! overlay semantics needed for patches, DLC, and mods: mount the base package
//! first, then patches/mods on top. A loose-file provider mounted on top is a
//! convenient way to override packaged assets during development.

const std = @import("std");
const provider_api = @import("Provider.zig");
const Provider = provider_api.Provider;

const AssetServer = @This();

allocator: std.mem.Allocator,
providers: std.ArrayList(Provider),

pub fn init(allocator: std.mem.Allocator) AssetServer {
    return .{ .allocator = allocator, .providers = .empty };
}

pub fn deinit(self: *AssetServer) void {
    self.providers.deinit(self.allocator);
    self.* = undefined;
}

/// Mount a provider on top of the stack (highest priority). The server does not
/// take ownership of the provider's backing storage — keep it alive and
/// `deinit` it yourself.
pub fn mount(self: *AssetServer, p: Provider) !void {
    try self.providers.append(self.allocator, p);
}

pub fn mountCount(self: *const AssetServer) usize {
    return self.providers.items.len;
}

/// Read `key`, trying providers from most- to least-recently mounted. Returns
/// the bytes from the first provider that has the asset (caller owns them), or
/// `error.AssetNotFound` if none do. Errors other than `AssetNotFound` from a
/// provider (e.g. corruption, missing key) are propagated immediately.
pub fn read(
    self: *const AssetServer,
    gpa: std.mem.Allocator,
    io: std.Io,
    key: []const u8,
) anyerror![]u8 {
    var i = self.providers.items.len;
    while (i > 0) {
        i -= 1;
        if (self.providers.items[i].read(gpa, io, key)) |bytes| {
            return bytes;
        } else |err| {
            if (err != provider_api.Error.AssetNotFound) return err;
        }
    }
    return provider_api.Error.AssetNotFound;
}

// ── Tests ─────────────────────────────────────────────────────────────────────

const oap = @import("open_asset_package");
const OapProvider = @import("OapProvider.zig");
const LooseFileProvider = @import("LooseFileProvider.zig");

fn buildPackage(gpa: std.mem.Allocator, entries: []const struct { path: []const u8, data: []const u8 }) ![]u8 {
    var w = oap.Writer.init(gpa);
    defer w.deinit();
    for (entries, 0..) |e, i| {
        var id: oap.AssetId = oap.nil_id;
        id[0] = @intCast(i + 1);
        try w.add(.{ .id = id, .vpath = e.path, .data = e.data });
    }
    return w.serialize();
}

test "reads from a mounted oap package" {
    const a = std.testing.allocator;
    const bytes = try buildPackage(a, &.{
        .{ .path = "hello.txt", .data = "hello world" },
    });

    var prov = try OapProvider.initFromBytes(a, bytes);
    defer prov.deinit();

    var server = AssetServer.init(a);
    defer server.deinit();
    try server.mount(prov.provider());

    const data = try server.read(a, std.testing.io, "hello.txt");
    defer a.free(data);
    try std.testing.expectEqualStrings("hello world", data);

    try std.testing.expectError(provider_api.Error.AssetNotFound, server.read(a, std.testing.io, "missing"));
}

test "later mount overrides earlier (patch semantics)" {
    const a = std.testing.allocator;
    const base_bytes = try buildPackage(a, &.{
        .{ .path = "shared.txt", .data = "BASE" },
        .{ .path = "only-base.txt", .data = "kept" },
    });
    const patch_bytes = try buildPackage(a, &.{
        .{ .path = "shared.txt", .data = "PATCHED" },
    });

    var base = try OapProvider.initFromBytes(a, base_bytes);
    defer base.deinit();
    var patch = try OapProvider.initFromBytes(a, patch_bytes);
    defer patch.deinit();

    var server = AssetServer.init(a);
    defer server.deinit();
    try server.mount(base.provider());
    try server.mount(patch.provider()); // higher priority

    const shared = try server.read(a, std.testing.io, "shared.txt");
    defer a.free(shared);
    try std.testing.expectEqualStrings("PATCHED", shared); // patch wins

    const fallthrough = try server.read(a, std.testing.io, "only-base.txt");
    defer a.free(fallthrough);
    try std.testing.expectEqualStrings("kept", fallthrough); // falls through to base
}

test "loose-file provider mounted over a package" {
    const a = std.testing.allocator;
    const bytes = try buildPackage(a, &.{
        .{ .path = "build.zig.zon", .data = "PACKAGED VERSION" },
    });
    var pkg = try OapProvider.initFromBytes(a, bytes);
    defer pkg.deinit();

    // The loose provider points at the repo root; reading "build.zig.zon" should
    // return the on-disk file, overriding the packaged copy.
    var loose = LooseFileProvider.init(".");

    var server = AssetServer.init(a);
    defer server.deinit();
    try server.mount(pkg.provider());
    try server.mount(loose.provider());

    const data = server.read(a, std.testing.io, "build.zig.zon") catch |err| {
        // If the test runner's cwd isn't the repo root, skip rather than fail.
        if (err == provider_api.Error.AssetNotFound) return error.SkipZigTest;
        return err;
    };
    defer a.free(data);
    try std.testing.expect(std.mem.indexOf(u8, data, "PACKAGED VERSION") == null);
    try std.testing.expect(std.mem.indexOf(u8, data, ".name") != null);
}
