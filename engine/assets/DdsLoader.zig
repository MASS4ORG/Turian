//! DDS texture container reader/cooker. Parses FourCC and DX10 extended
//! headers, decodes BC1/BC3/BC4/BC5/BC7 and uncompressed RGBA8 into
//! `ktx2.Image`. `cook` bakes in sRGB tagging and DirectX-convention
//! normal map green-channel inversion.
const std = @import("std");
const ktx2 = @import("ktx2");

pub const Error = error{
    NotDds,
    Truncated,
    UnsupportedFormat,
    UnsupportedDimension,
};

const dds_magic = [4]u8{ 'D', 'D', 'S', ' ' };
const LEGACY_HEADER_LEN = 128; // magic (4) + DDS_HEADER (124)
const DX10_HEADER_LEN = 20;

const DDPF_FOURCC: u32 = 0x4;
const DDSCAPS2_CUBEMAP: u32 = 0x200;

fn fourCC(a: u8, b: u8, c: u8, d: u8) u32 {
    return @as(u32, a) | (@as(u32, b) << 8) | (@as(u32, c) << 16) | (@as(u32, d) << 24);
}

const FOURCC_DXT1 = fourCC('D', 'X', 'T', '1');
const FOURCC_DXT5 = fourCC('D', 'X', 'T', '5');
const FOURCC_ATI1 = fourCC('A', 'T', 'I', '1');
const FOURCC_BC4U = fourCC('B', 'C', '4', 'U');
const FOURCC_ATI2 = fourCC('A', 'T', 'I', '2');
const FOURCC_BC5U = fourCC('B', 'C', '5', 'U');
const FOURCC_DX10 = fourCC('D', 'X', '1', '0');

const DXGI_FORMAT_R8G8B8A8_UNORM: u32 = 28;
const DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: u32 = 29;
const DXGI_FORMAT_BC1_UNORM: u32 = 71;
const DXGI_FORMAT_BC1_UNORM_SRGB: u32 = 72;
const DXGI_FORMAT_BC3_UNORM: u32 = 77;
const DXGI_FORMAT_BC3_UNORM_SRGB: u32 = 78;
const DXGI_FORMAT_BC4_UNORM: u32 = 80;
const DXGI_FORMAT_BC5_UNORM: u32 = 83;
const DXGI_FORMAT_BC7_UNORM: u32 = 98;
const DXGI_FORMAT_BC7_UNORM_SRGB: u32 = 99;

fn rdU32(b: []const u8, off: usize) u32 {
    return std.mem.readInt(u32, b[off..][0..4], .little);
}

fn formatFromFourCC(cc: u32) ?ktx2.Format {
    return switch (cc) {
        FOURCC_DXT1 => .bc1_rgb_unorm,
        FOURCC_DXT5 => .bc3_unorm,
        FOURCC_ATI1, FOURCC_BC4U => .bc4_unorm,
        FOURCC_ATI2, FOURCC_BC5U => .bc5_unorm,
        else => null,
    };
}

fn formatFromDxgi(fmt: u32) ?ktx2.Format {
    return switch (fmt) {
        DXGI_FORMAT_R8G8B8A8_UNORM => .rgba8_unorm,
        DXGI_FORMAT_R8G8B8A8_UNORM_SRGB => .rgba8_srgb,
        DXGI_FORMAT_BC1_UNORM => .bc1_rgb_unorm,
        DXGI_FORMAT_BC1_UNORM_SRGB => .bc1_rgb_srgb,
        DXGI_FORMAT_BC3_UNORM => .bc3_unorm,
        DXGI_FORMAT_BC3_UNORM_SRGB => .bc3_srgb,
        DXGI_FORMAT_BC4_UNORM => .bc4_unorm,
        DXGI_FORMAT_BC5_UNORM => .bc5_unorm,
        DXGI_FORMAT_BC7_UNORM => .bc7_unorm,
        DXGI_FORMAT_BC7_UNORM_SRGB => .bc7_srgb,
        else => null,
    };
}

fn dxgiFromFormat(fmt: ktx2.Format) u32 {
    return switch (fmt) {
        .rgba8_unorm => DXGI_FORMAT_R8G8B8A8_UNORM,
        .rgba8_srgb => DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
        .bc1_rgb_unorm => DXGI_FORMAT_BC1_UNORM,
        .bc1_rgb_srgb => DXGI_FORMAT_BC1_UNORM_SRGB,
        .bc3_unorm => DXGI_FORMAT_BC3_UNORM,
        .bc3_srgb => DXGI_FORMAT_BC3_UNORM_SRGB,
        .bc4_unorm => DXGI_FORMAT_BC4_UNORM,
        .bc5_unorm => DXGI_FORMAT_BC5_UNORM,
        .bc7_unorm => DXGI_FORMAT_BC7_UNORM,
        .bc7_srgb => DXGI_FORMAT_BC7_UNORM_SRGB,
    };
}

/// sRGB counterpart of `fmt`, or null if the format has none (BC4/BC5 are
/// always linear single/dual-channel data).
fn srgbVariant(fmt: ktx2.Format) ?ktx2.Format {
    return switch (fmt) {
        .rgba8_unorm => .rgba8_srgb,
        .bc1_rgb_unorm => .bc1_rgb_srgb,
        .bc3_unorm => .bc3_srgb,
        .bc7_unorm => .bc7_srgb,
        else => null,
    };
}

/// True if `bytes` begins with the DDS magic ("DDS ").
pub fn isDds(bytes: []const u8) bool {
    return bytes.len >= dds_magic.len and std.mem.eql(u8, bytes[0..dds_magic.len], &dds_magic);
}

const HeaderInfo = struct {
    width: u32,
    height: u32,
    mip_count: u32,
    format: ktx2.Format,
    data_offset: usize,
    needs_bgra_swizzle: bool,
};

/// Parse a DDS header (legacy `DDS_PIXELFORMAT` or DX10 extended header).
/// Cubemaps and texture arrays are detected and rejected: `Texture` only
/// models a single 2D image with mips.
fn parseHeader(bytes: []const u8) Error!HeaderInfo {
    if (!isDds(bytes)) return Error.NotDds;
    if (bytes.len < LEGACY_HEADER_LEN or rdU32(bytes, 4) != 124) return Error.Truncated;

    const flags = rdU32(bytes, 8);
    const height = rdU32(bytes, 12);
    const width = rdU32(bytes, 16);
    const mip_count: u32 = if (flags & 0x20000 != 0) @max(rdU32(bytes, 28), 1) else 1;
    if (width == 0 or height == 0) return Error.UnsupportedDimension;

    const caps2 = rdU32(bytes, 112);
    if (caps2 & DDSCAPS2_CUBEMAP != 0) return Error.UnsupportedFormat;

    const pf_flags = rdU32(bytes, 76 + 4);
    const fourcc = rdU32(bytes, 76 + 8);

    var format: ktx2.Format = undefined;
    var data_offset: usize = LEGACY_HEADER_LEN;
    var needs_bgra_swizzle = false;

    if (pf_flags & DDPF_FOURCC != 0 and fourcc == FOURCC_DX10) {
        if (bytes.len < LEGACY_HEADER_LEN + DX10_HEADER_LEN) return Error.Truncated;
        const resource_dim = rdU32(bytes, LEGACY_HEADER_LEN + 4);
        if (resource_dim != 3) return Error.UnsupportedDimension; // 3 = TEXTURE2D
        const array_size = rdU32(bytes, LEGACY_HEADER_LEN + 12);
        if (array_size > 1) return Error.UnsupportedFormat;
        format = formatFromDxgi(rdU32(bytes, LEGACY_HEADER_LEN)) orelse return Error.UnsupportedFormat;
        data_offset = LEGACY_HEADER_LEN + DX10_HEADER_LEN;
    } else if (pf_flags & DDPF_FOURCC != 0) {
        format = formatFromFourCC(fourcc) orelse return Error.UnsupportedFormat;
    } else {
        // Legacy uncompressed RGB/RGBA: only the common 32bpp mask layouts.
        const bitcount = rdU32(bytes, 76 + 12);
        const rmask = rdU32(bytes, 76 + 16);
        const gmask = rdU32(bytes, 76 + 20);
        const bmask = rdU32(bytes, 76 + 24);
        if (bitcount != 32 or gmask != 0x0000ff00) return Error.UnsupportedFormat;
        if (rmask == 0x000000ff and bmask == 0x00ff0000) {
            format = .rgba8_unorm;
        } else if (rmask == 0x00ff0000 and bmask == 0x000000ff) {
            format = .rgba8_unorm;
            needs_bgra_swizzle = true;
        } else return Error.UnsupportedFormat;
    }

    return .{
        .width = width,
        .height = height,
        .mip_count = mip_count,
        .format = format,
        .data_offset = data_offset,
        .needs_bgra_swizzle = needs_bgra_swizzle,
    };
}

/// Decode a DDS container into a GPU-ready `ktx2.Image`. Block-compressed
/// payloads are copied through untouched (no re-encode); legacy BGRA-ordered
/// uncompressed data is swizzled to RGBA to match the engine's convention.
pub fn decode(allocator: std.mem.Allocator, bytes: []const u8) (Error || std.mem.Allocator.Error)!ktx2.Image {
    const h = try parseHeader(bytes);

    const levels = try allocator.alloc(ktx2.Level, h.mip_count);
    errdefer allocator.free(levels);

    var w = h.width;
    var ht = h.height;
    var total: usize = 0;
    for (0..h.mip_count) |i| {
        const size = h.format.levelSize(w, ht);
        levels[i] = .{ .offset = total, .len = size, .width = w, .height = ht };
        total += size;
        w = @max(1, w / 2);
        ht = @max(1, ht / 2);
    }

    if (h.data_offset + total > bytes.len) return Error.Truncated;

    const data = try allocator.dupe(u8, bytes[h.data_offset..][0..total]);
    errdefer allocator.free(data);
    if (h.needs_bgra_swizzle) swizzleBgraToRgba(data);

    return .{
        .format = h.format,
        .width = h.width,
        .height = h.height,
        .levels = levels,
        .data = data,
        .allocator = allocator,
    };
}

fn swizzleBgraToRgba(data: []u8) void {
    var i: usize = 0;
    while (i + 4 <= data.len) : (i += 4) std.mem.swap(u8, &data[i], &data[i + 2]);
}

/// Import-time rewrite options (see `cook`).
pub const CookOptions = struct {
    /// Tag the texture as sRGB (albedo/emissive) rather than linear (the
    /// default for normal/ORM data). Legacy FourCC has no sRGB bit, so this
    /// upgrades the container to a DX10 header when the format has an sRGB
    /// counterpart.
    srgb: bool = false,
    /// Invert the green (Y) channel of BC5 blocks, for DirectX-convention
    /// normal maps authored with the opposite Y handedness. No-op for any
    /// other format.
    flip_green_channel: bool = false,
};

/// Rewrite a DDS container per `options`. Returns a copy of `bytes` unchanged
/// when no rewrite is needed. Caller owns the returned slice.
pub fn cook(allocator: std.mem.Allocator, bytes: []const u8, options: CookOptions) (Error || std.mem.Allocator.Error)![]u8 {
    const h = try parseHeader(bytes);

    const target_format = if (options.srgb) (srgbVariant(h.format) orelse h.format) else h.format;
    const needs_retag = target_format != h.format;
    const needs_flip = options.flip_green_channel and h.format == .bc5_unorm;

    if (!needs_retag and !needs_flip) return allocator.dupe(u8, bytes);

    const pixel_data = bytes[h.data_offset..];
    const header_len: usize = if (needs_retag) LEGACY_HEADER_LEN + DX10_HEADER_LEN else h.data_offset;
    const out = try allocator.alloc(u8, header_len + pixel_data.len);
    errdefer allocator.free(out);

    @memcpy(out[0..76], bytes[0..76]);
    if (needs_retag) {
        writeDx10Ddpf(out[76..108]);
        @memcpy(out[108..LEGACY_HEADER_LEN], bytes[108..LEGACY_HEADER_LEN]);
        writeDx10Header(out[LEGACY_HEADER_LEN..header_len], target_format);
    } else {
        @memcpy(out[76..header_len], bytes[76..header_len]);
    }
    @memcpy(out[header_len..], pixel_data);

    if (needs_flip) flipBc5GreenChannel(out[header_len..]);

    return out;
}

fn writeDx10Ddpf(buf: []u8) void {
    std.mem.writeInt(u32, buf[0..4], 32, .little); // dwSize
    std.mem.writeInt(u32, buf[4..8], DDPF_FOURCC, .little); // dwFlags
    @memcpy(buf[8..12], "DX10");
    @memset(buf[12..32], 0);
}

fn writeDx10Header(buf: []u8, format: ktx2.Format) void {
    std.mem.writeInt(u32, buf[0..4], dxgiFromFormat(format), .little); // dxgiFormat
    std.mem.writeInt(u32, buf[4..8], 3, .little); // resourceDimension = TEXTURE2D
    std.mem.writeInt(u32, buf[8..12], 0, .little); // miscFlag
    std.mem.writeInt(u32, buf[12..16], 1, .little); // arraySize
    std.mem.writeInt(u32, buf[16..20], 0, .little); // miscFlags2 (alpha mode unknown)
}

/// Inverts (255 - v) every texel of a single-channel BC4 sub-block in place,
/// by swap-negating its two endpoints and permuting the 3-bit indices to
/// match — exact and lossless, no decode/recompress needed. Works in both the
/// 8-value ramp mode (e0 > e1) and the 6-value + fixed 0/255 mode (e0 <= e1);
/// each has its own index permutation since the two modes are not compatible.
fn flipBc4Channel(block: *[8]u8) void {
    const e0 = block[0];
    const e1 = block[1];
    const mode_a = e0 > e1;
    block[0] = 255 - e1;
    block[1] = 255 - e0;

    const perm: [8]u3 = if (mode_a)
        .{ 1, 0, 7, 6, 5, 4, 3, 2 }
    else
        .{ 1, 0, 5, 4, 3, 2, 7, 6 };

    // 16 indices, 3 bits each, packed little-endian across 6 bytes.
    var bits: u64 = 0;
    for (0..6) |i| bits |= @as(u64, block[2 + i]) << @intCast(i * 8);

    var new_bits: u64 = 0;
    for (0..16) |t| {
        const idx: u3 = @intCast((bits >> @intCast(t * 3)) & 0x7);
        new_bits |= @as(u64, perm[idx]) << @intCast(t * 3);
    }
    for (0..6) |i| block[2 + i] = @intCast((new_bits >> @intCast(i * 8)) & 0xff);
}

fn flipBc5GreenChannel(data: []u8) void {
    var off: usize = 0;
    while (off + 16 <= data.len) : (off += 16) flipBc4Channel(data[off + 8 ..][0..8]);
}

// ── Tests ─────────────────────────────────────────────────────────────────────

fn makeLegacyHeader(fourcc: [4]u8, width: u32, height: u32, mip_count: u32) [LEGACY_HEADER_LEN]u8 {
    var b: [LEGACY_HEADER_LEN]u8 = @splat(0);
    @memcpy(b[0..4], &dds_magic);
    std.mem.writeInt(u32, b[4..8], 124, .little); // dwSize
    std.mem.writeInt(u32, b[8..12], 0x1007 | 0x20000, .little); // CAPS|WIDTH|HEIGHT|PIXELFORMAT|MIPMAPCOUNT
    std.mem.writeInt(u32, b[12..16], height, .little);
    std.mem.writeInt(u32, b[16..20], width, .little);
    std.mem.writeInt(u32, b[28..32], mip_count, .little);
    std.mem.writeInt(u32, b[76..80], 32, .little); // ddspf.dwSize
    std.mem.writeInt(u32, b[80..84], DDPF_FOURCC, .little); // ddspf.dwFlags
    @memcpy(b[84..88], &fourcc);
    return b;
}

test "isDds detects the magic and rejects other containers" {
    const header = makeLegacyHeader(.{ 'D', 'X', 'T', '1' }, 4, 4, 1);
    try std.testing.expect(isDds(&header));
    try std.testing.expect(!isDds("not a dds file"));
}

test "decode reads a single-mip BC1 (DXT1) block-copy" {
    var bytes: [LEGACY_HEADER_LEN + 8]u8 = undefined;
    @memcpy(bytes[0..LEGACY_HEADER_LEN], &makeLegacyHeader(.{ 'D', 'X', 'T', '1' }, 4, 4, 1));
    @memset(bytes[LEGACY_HEADER_LEN..], 0xAB);

    var img = try decode(std.testing.allocator, &bytes);
    defer img.deinit();

    try std.testing.expectEqual(ktx2.Format.bc1_rgb_unorm, img.format);
    try std.testing.expectEqual(@as(u32, 4), img.width);
    try std.testing.expectEqual(@as(u32, 4), img.height);
    try std.testing.expectEqual(@as(usize, 1), img.levels.len);
    try std.testing.expectEqualSlices(u8, &[_]u8{0xAB} ** 8, img.data);
}

test "decode builds a sequential mip chain for BC3 (DXT5)" {
    // 8x8 BC3: mip0 = 4 blocks (64B), mip1 (4x4) = 1 block (16B), mip2 (2x2->1 block)=16B, mip3(1x1)=16B.
    const w = 8;
    const h = 8;
    const mip0_size = ktx2.Format.bc3_unorm.levelSize(w, h);
    const mip1_size = ktx2.Format.bc3_unorm.levelSize(4, 4);
    const mip2_size = ktx2.Format.bc3_unorm.levelSize(2, 2);
    const mip3_size = ktx2.Format.bc3_unorm.levelSize(1, 1);
    const total = mip0_size + mip1_size + mip2_size + mip3_size;

    const bytes = try std.testing.allocator.alloc(u8, LEGACY_HEADER_LEN + total);
    defer std.testing.allocator.free(bytes);
    @memcpy(bytes[0..LEGACY_HEADER_LEN], &makeLegacyHeader(.{ 'D', 'X', 'T', '5' }, w, h, 4));
    @memset(bytes[LEGACY_HEADER_LEN..], 0);

    var img = try decode(std.testing.allocator, bytes);
    defer img.deinit();

    try std.testing.expectEqual(@as(usize, 4), img.levels.len);
    try std.testing.expectEqual(@as(u32, 8), img.levels[0].width);
    try std.testing.expectEqual(@as(u32, 1), img.levels[3].width);
    try std.testing.expectEqual(total, img.data.len);
}

test "decode rejects cubemaps" {
    var header = makeLegacyHeader(.{ 'D', 'X', 'T', '1' }, 4, 4, 1);
    std.mem.writeInt(u32, header[112..116], DDSCAPS2_CUBEMAP, .little);
    var bytes: [LEGACY_HEADER_LEN + 8]u8 = undefined;
    @memcpy(bytes[0..LEGACY_HEADER_LEN], &header);
    try std.testing.expectError(Error.UnsupportedFormat, decode(std.testing.allocator, &bytes));
}

test "cook tags sRGB by upgrading the legacy FourCC to a DX10 header" {
    var bytes: [LEGACY_HEADER_LEN + 8]u8 = undefined;
    @memcpy(bytes[0..LEGACY_HEADER_LEN], &makeLegacyHeader(.{ 'D', 'X', 'T', '1' }, 4, 4, 1));
    @memset(bytes[LEGACY_HEADER_LEN..], 0x55);

    const cooked = try cook(std.testing.allocator, &bytes, .{ .srgb = true });
    defer std.testing.allocator.free(cooked);

    var img = try decode(std.testing.allocator, cooked);
    defer img.deinit();
    try std.testing.expectEqual(ktx2.Format.bc1_rgb_srgb, img.format);
    try std.testing.expectEqualSlices(u8, &[_]u8{0x55} ** 8, img.data);
}

test "cook is a no-op copy when no rewrite is requested" {
    var bytes: [LEGACY_HEADER_LEN + 8]u8 = undefined;
    @memcpy(bytes[0..LEGACY_HEADER_LEN], &makeLegacyHeader(.{ 'D', 'X', 'T', '1' }, 4, 4, 1));
    @memset(bytes[LEGACY_HEADER_LEN..], 0x77);

    const cooked = try cook(std.testing.allocator, &bytes, .{});
    defer std.testing.allocator.free(cooked);
    try std.testing.expectEqualSlices(u8, &bytes, cooked);
}

test "flipBc4Channel inverts every texel value and is its own inverse" {
    // e0=200 > e1=50 (8-value ramp mode).
    var block = [8]u8{ 200, 50, 0b10110100, 0b01011110, 0b11100001, 0b00111010, 0b01101101, 0b10010011 };
    const original = block;

    flipBc4Channel(&block);
    try std.testing.expect(block[0] != original[0] or block[1] != original[1]);

    flipBc4Channel(&block);
    try std.testing.expectEqualSlices(u8, &original, &block);
}

test "flipBc4Channel round-trips the 6-value + fixed-point mode too" {
    // e0=50 <= e1=200 (6-value ramp + fixed 0/255 mode).
    var block = [8]u8{ 50, 200, 0b11000110, 0b00101101, 0b10011011, 0b01110100, 0b00010111, 0b11101000 };
    const original = block;

    flipBc4Channel(&block);
    flipBc4Channel(&block);
    try std.testing.expectEqualSlices(u8, &original, &block);
}

/// Reference BC4 single-channel decode (spec formula), used only to check
/// `flipBc4Channel` produces exact (255 - v) values, not just an involution.
fn decodeBc4Value(block: *const [8]u8, texel: u4) u8 {
    const e0 = block[0];
    const e1 = block[1];
    var bits: u64 = 0;
    for (0..6) |i| bits |= @as(u64, block[2 + i]) << @intCast(i * 8);
    const idx: u3 = @intCast((bits >> @intCast(@as(u6, texel) * 3)) & 0x7);

    if (e0 > e1) {
        return switch (idx) {
            0 => e0,
            1 => e1,
            else => @intCast((@as(u32, 8 - @as(u32, idx)) * e0 + @as(u32, idx - 1) * e1 + 3) / 7),
        };
    }
    return switch (idx) {
        0 => e0,
        1 => e1,
        6 => 0,
        7 => 255,
        else => @intCast((@as(u32, 6 - @as(u32, idx)) * e0 + @as(u32, idx - 1) * e1 + 2) / 5),
    };
}

test "flipBc4Channel inverts the actual decoded value at every texel (8-value mode)" {
    var block = [8]u8{ 200, 50, 0b10110100, 0b01011110, 0b11100001, 0b00111010, 0b01101101, 0b10010011 };
    var before: [16]u8 = undefined;
    for (0..16) |t| before[t] = decodeBc4Value(&block, @intCast(t));

    flipBc4Channel(&block);

    for (0..16) |t| {
        const after = decodeBc4Value(&block, @intCast(t));
        try std.testing.expectEqual(@as(u8, 255 - before[t]), after);
    }
}

test "flipBc4Channel inverts the actual decoded value at every texel (fixed-point mode)" {
    // Indices covering the ramp (0-5) and the two fixed points (6=0, 7=255).
    var block = [8]u8{ 50, 200, 0b11000110, 0b00101101, 0b10011011, 0b01110100, 0b00010111, 0b11101000 };
    var before: [16]u8 = undefined;
    for (0..16) |t| before[t] = decodeBc4Value(&block, @intCast(t));

    flipBc4Channel(&block);

    for (0..16) |t| {
        const after = decodeBc4Value(&block, @intCast(t));
        try std.testing.expectEqual(@as(u8, 255 - before[t]), after);
    }
}

fn decodeFixture(path: []const u8) !ktx2.Image {
    const bytes = std.Io.Dir.cwd().readFileAlloc(std.testing.io, path, std.testing.allocator, .unlimited) catch
        return error.SkipZigTest;
    defer std.testing.allocator.free(bytes);
    return decode(std.testing.allocator, bytes);
}

test "decodes a real BC1 (DXT1) Bistro base color texture" {
    const path = "/media/work/dev/mega4/turian-samples/bistro/assets/Textures/Paris_StreetSign_01_BaseColor.dds";
    var img = try decodeFixture(path);
    defer img.deinit();
    try std.testing.expectEqual(ktx2.Format.bc1_rgb_unorm, img.format);
    try std.testing.expect(img.levels.len >= 1);
}

test "decodes a real BC3 (DXT5) Bistro specular texture" {
    const path = "/media/work/dev/mega4/turian-samples/bistro/assets/Textures/MASTER_Focus_Ornament_Specular.dds";
    var img = try decodeFixture(path);
    defer img.deinit();
    try std.testing.expectEqual(ktx2.Format.bc3_unorm, img.format);
    try std.testing.expect(img.levels.len >= 1);
}

test "decodes a real BC5 (ATI2) Bistro normal map" {
    const path = "/media/work/dev/mega4/turian-samples/bistro/assets/Textures/Shopsign_Book_Store_Emissive_Normal.dds";
    var img = try decodeFixture(path);
    defer img.deinit();
    try std.testing.expectEqual(ktx2.Format.bc5_unorm, img.format);
    try std.testing.expect(img.levels.len >= 1);
}

test {
    std.testing.refAllDecls(@This());
}
