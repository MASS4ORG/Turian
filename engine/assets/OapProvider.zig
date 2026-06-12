//! Asset provider backed by a single `.oap` package (Open Asset Package).
//!
//! Used in release builds, where assets are cooked and packed into one or more
//! `.oap` files. Mount several with `AssetServer` to support a base package plus
//! DLC, patches, or mods. Assets are looked up by their virtual path.

const std = @import("std");
const oap = @import("open_asset_package");
const provider_api = @import("Provider.zig");
const Provider = provider_api.Provider;

const OapProvider = @This();

reader: oap.Reader,
/// Verify each asset's CRC-32 on read. Disable for a small speed win once you
/// trust the package's integrity.
verify: bool = true,

/// Open a package from a file on disk.
pub fn initFromFile(io: std.Io, gpa: std.mem.Allocator, path: []const u8) !OapProvider {
    return .{ .reader = try oap.Reader.openFile(io, gpa, path) };
}

/// Open a package already in memory. Takes ownership of `bytes`.
pub fn initFromBytes(gpa: std.mem.Allocator, bytes: []u8) !OapProvider {
    return .{ .reader = try oap.Reader.initOwned(gpa, bytes) };
}

pub fn deinit(self: *OapProvider) void {
    self.reader.deinit();
}

/// Provide the key for decrypting encrypted assets in this package.
pub fn setKey(self: *OapProvider, key: oap.Key) void {
    self.reader.setKey(key);
}

pub fn provider(self: *OapProvider) Provider {
    return .{ .ptr = self, .vtable = &vtable };
}

/// Bytes plus the originating virtual path (so callers can recover the file
/// extension for format dispatch). `bytes` are caller-owned; `ext` borrows the
/// package buffer and stays valid while this provider is alive.
pub const ByIdResult = struct { bytes: []u8, ext: []const u8 };

/// Number of assets in the package (for type-filtered enumeration).
pub fn assetCount(self: *const OapProvider) usize {
    return self.reader.count();
}

/// The id and packaged asset-type tag of the i-th asset. The tag is the editor's
/// `@intFromEnum(AssetType)` value, so callers that know that enum can filter by
/// type without the engine depending on the editor.
pub fn assetEntryAt(self: *const OapProvider, i: usize) struct { id: oap.AssetId, asset_type: u8 } {
    const e = self.reader.entryAt(i);
    return .{ .id = e.asset_id, .asset_type = e.asset_type };
}

/// Look up an asset by its 128-bit id (e.g. an asset GUID) and decode it.
/// Returns null if the package has no such asset or it fails to decode.
pub fn readById(self: *OapProvider, gpa: std.mem.Allocator, id: oap.AssetId) ?ByIdResult {
    const entry = self.reader.findById(id) orelse return null;
    const bytes = self.reader.readAsset(gpa, entry, self.verify) catch return null;
    const vpath = self.reader.virtualPath(entry);
    return .{ .bytes = bytes, .ext = extensionOf(vpath) };
}

/// Look up an asset by its virtual path and decode it.
/// Returns null if not found or decode fails. Caller owns the returned slice.
pub fn readByPath(self: *OapProvider, gpa: std.mem.Allocator, vpath: []const u8) ?[]u8 {
    const entry = self.reader.findByPath(vpath) orelse return null;
    return self.reader.readAsset(gpa, entry, self.verify) catch null;
}

fn extensionOf(path: []const u8) []const u8 {
    if (std.mem.lastIndexOfScalar(u8, path, '.')) |dot| return path[dot..];
    return "";
}

const vtable = Provider.VTable{ .read = readFn };

fn readFn(
    ptr: *anyopaque,
    gpa: std.mem.Allocator,
    io: std.Io,
    key: []const u8,
) anyerror![]u8 {
    _ = io; // package is already resident; no I/O needed
    const self: *OapProvider = @ptrCast(@alignCast(ptr));
    const entry = self.reader.findByPath(key) orelse return provider_api.Error.AssetNotFound;
    return self.reader.readAsset(gpa, entry, self.verify);
}
