/// Image loader. Decodes KTX2 containers (BCn / Basis-transcoded, via the `ktx2`
/// module) or, for everything stb_image understands (PNG/JPEG/…), RGBA8 pixels.
const std = @import("std");
const ktx2 = @import("ktx2");
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
/// package). KTX2 is detected by its identifier; otherwise stb_image sniffs the
/// format, so no extension is required.
pub fn loadFromMemory(allocator: std.mem.Allocator, bytes: []const u8) !Texture {
    if (ktx2.isKtx2(bytes)) return fromKtx2(allocator, bytes);

    var w: c_int = 0;
    var h: c_int = 0;
    var ch: c_int = 0;
    const pixels = stbi_load_from_memory(bytes.ptr, @intCast(bytes.len), &w, &h, &ch, 4) orelse
        return error.ImageLoadFailed;
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

/// Decode a KTX2 container into a (possibly block-compressed, multi-mip)
/// Texture. Ownership of the decoded buffers is moved into the Texture.
fn fromKtx2(allocator: std.mem.Allocator, bytes: []const u8) !Texture {
    const img = try ktx2.decode(allocator, bytes);
    // Move the decoded buffers into the Texture; do not deinit `img`.
    return Texture{
        .data = img.data,
        .width = img.width,
        .height = img.height,
        .format = img.format,
        .mips = img.levels,
        .allocator = allocator,
        .owns_mips = true,
    };
}

test "decodes a Basis KTX2 into a compressed texture" {
    // Uses a local glTF-Sample-Assets file when present; skipped otherwise.
    const path = "/media/referencia/programming/glTF-Sample-Assets/Models/ChronographWatch/glTF-KTX-BasisU/khronos_basecolor.ktx2";
    const bytes = std.Io.Dir.cwd().readFileAlloc(std.testing.io, path, std.testing.allocator, .unlimited) catch
        return error.SkipZigTest;
    defer std.testing.allocator.free(bytes);

    var tex = try loadFromMemory(std.testing.allocator, bytes);
    defer tex.deinit();
    try std.testing.expect(tex.isCompressed());
    try std.testing.expect(tex.mips.len >= 1);
    try std.testing.expect(tex.width > 0 and tex.height > 0);
}

pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Texture {
    var file = try std.Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const file_data = try reader.interface.allocRemaining(allocator, .unlimited);
    defer allocator.free(file_data);

    return loadFromMemory(allocator, file_data);
}
