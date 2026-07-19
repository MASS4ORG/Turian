/// Image loader. Decodes KTX2 or DDS containers (BCn / Basis-transcoded, via
/// the `ktx2` module and `DdsLoader`) or, for everything stb_image understands
/// (PNG/JPEG/…), RGBA8 pixels.
const std = @import("std");
const ktx2 = @import("ktx2");
const dds = @import("DdsLoader.zig");
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

/// Prefix marking a color-space tag baked in at import time (see `wrapColorTag`).
/// Only stb_image containers (PNG/JPEG/…) need it: KTX2/DDS already carry their
/// own format field, and the stb_image path otherwise always decodes to the
/// default linear `.rgba8_unorm`.
const color_tag_magic = "TCS1";

/// Prefix `bytes` with a tiny envelope tagging the color space the image
/// should decode as. `loadFromMemory` strips it and upgrades the resulting
/// `Texture.format` to `.rgba8_srgb` when `srgb` is set. Caller owns the
/// returned slice.
pub fn wrapColorTag(allocator: std.mem.Allocator, bytes: []const u8, srgb: bool) ![]u8 {
    const out = try allocator.alloc(u8, 5 + bytes.len);
    @memcpy(out[0..4], color_tag_magic);
    out[4] = @intFromBool(srgb);
    @memcpy(out[5..], bytes);
    return out;
}

/// Decode an image from an in-memory byte buffer (e.g. supplied by an asset
/// package). KTX2 is detected by its identifier; otherwise stb_image sniffs the
/// format, so no extension is required.
pub fn loadFromMemory(allocator: std.mem.Allocator, bytes: []const u8) !Texture {
    if (bytes.len >= 5 and std.mem.eql(u8, bytes[0..4], color_tag_magic)) {
        const srgb = bytes[4] != 0;
        var tex = try loadFromMemory(allocator, bytes[5..]);
        if (srgb and tex.format == .rgba8_unorm) tex.format = .rgba8_srgb;
        return tex;
    }
    if (ktx2.isKtx2(bytes)) return fromKtx2(allocator, bytes);
    if (dds.isDds(bytes)) return fromDds(allocator, bytes);

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

/// Decode a DDS container into a (possibly block-compressed, multi-mip)
/// Texture. Ownership of the decoded buffers is moved into the Texture.
fn fromDds(allocator: std.mem.Allocator, bytes: []const u8) !Texture {
    const img = try dds.decode(allocator, bytes);
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

test "wrapColorTag upgrades an uncompressed source to rgba8_srgb" {
    // Minimal legacy-header DDS, uncompressed RGBA8 (R8G8B8A8 bitmasks), 2x2, no mips.
    var dds_bytes: [128 + 2 * 2 * 4]u8 = @splat(0);
    @memcpy(dds_bytes[0..4], "DDS ");
    std.mem.writeInt(u32, dds_bytes[4..8], 124, .little); // dwSize
    std.mem.writeInt(u32, dds_bytes[8..12], 0x1007, .little); // CAPS|WIDTH|HEIGHT|PIXELFORMAT
    std.mem.writeInt(u32, dds_bytes[12..16], 2, .little); // height
    std.mem.writeInt(u32, dds_bytes[16..20], 2, .little); // width
    std.mem.writeInt(u32, dds_bytes[76..80], 32, .little); // ddspf.dwSize
    std.mem.writeInt(u32, dds_bytes[80..84], 0x41, .little); // ddspf.dwFlags: RGB|ALPHAPIXELS
    std.mem.writeInt(u32, dds_bytes[88..92], 32, .little); // ddspf.dwRGBBitCount
    std.mem.writeInt(u32, dds_bytes[92..96], 0x000000ff, .little); // dwRBitMask
    std.mem.writeInt(u32, dds_bytes[96..100], 0x0000ff00, .little); // dwGBitMask
    std.mem.writeInt(u32, dds_bytes[100..104], 0x00ff0000, .little); // dwBBitMask
    @memset(dds_bytes[128..], 0x42);

    const wrapped = try wrapColorTag(std.testing.allocator, &dds_bytes, true);
    defer std.testing.allocator.free(wrapped);

    var tex = try loadFromMemory(std.testing.allocator, wrapped);
    defer tex.deinit();
    try std.testing.expectEqual(ktx2.Format.rgba8_srgb, tex.format);
    try std.testing.expectEqual(@as(u32, 2), tex.width);
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

/// Load an image from a file path. Detects KTX2 by identifier; otherwise
/// delegates to stb_image for PNG/JPEG/etc.
pub fn load(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Texture {
    var file = try std.Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const file_data = try reader.interface.allocRemaining(allocator, .unlimited);
    defer allocator.free(file_data);

    return loadFromMemory(allocator, file_data);
}
