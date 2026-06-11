/// Image loader backed by stb_image.
/// Reads via std.Io, decodes to RGBA8, copies to a Zig-managed buffer.
const std = @import("std");
const Texture = @import("Texture.zig").Texture;

extern fn stbi_load_from_memory(
    buf: [*]const u8,
    len: c_int,
    x: *c_int,
    y: *c_int,
    ch: *c_int,
    desired: c_int,
) ?[*]u8;
extern fn stbi_image_free(data: ?*anyopaque) void;

/// Decode an image from an in-memory byte buffer (e.g. supplied by an asset
/// package) to an RGBA8 texture. stb_image sniffs the format from the bytes, so
/// no extension is required.
pub fn loadFromMemory(allocator: std.mem.Allocator, bytes: []const u8) !Texture {
    var w: c_int = 0;
    var h: c_int = 0;
    var ch: c_int = 0;
    const pixels = stbi_load_from_memory(
        bytes.ptr,
        @intCast(bytes.len),
        &w,
        &h,
        &ch,
        4,
    ) orelse return error.ImageLoadFailed;
    defer stbi_image_free(pixels);

    const n_bytes = @as(usize, @intCast(w)) * @as(usize, @intCast(h)) * 4;
    const data = try allocator.alloc(u8, n_bytes);
    @memcpy(data, pixels[0..n_bytes]);

    return Texture{
        .data = data,
        .width = @intCast(w),
        .height = @intCast(h),
        .allocator = allocator,
    };
}

pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Texture {
    var file = try std.Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const file_data = try reader.interface.allocRemainingAlignedSentinel(
        allocator,
        .unlimited,
        .@"1",
        0,
    );
    defer allocator.free(file_data);

    var w: c_int = 0;
    var h: c_int = 0;
    var ch: c_int = 0;
    const pixels = stbi_load_from_memory(
        file_data.ptr,
        @intCast(file_data.len),
        &w,
        &h,
        &ch,
        4,
    ) orelse return error.ImageLoadFailed;
    defer stbi_image_free(pixels);

    const n_bytes = @as(usize, @intCast(w)) * @as(usize, @intCast(h)) * 4;
    const data = try allocator.alloc(u8, n_bytes);
    @memcpy(data, pixels[0..n_bytes]);

    return Texture{
        .data = data,
        .width = @intCast(w),
        .height = @intCast(h),
        .allocator = allocator,
    };
}
