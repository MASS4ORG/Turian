//! Radiance HDR (`.hdr`/`.pic`, RGBE) image loader — decodes an equirectangular
//! environment map into linear float RGB for image-based lighting. Supports
//! both legacy flat/RLE scanlines and the standard "new-style" per-channel RLE
//! scanline encoding real-world HDRI captures use (e.g. Poly Haven exports).
const std = @import("std");

pub const Error = error{
    NotHdr,
    NotEnvelope,
    Truncated,
    UnsupportedFormat,
    UnsupportedDimension,
};

const magic_radiance = "#?RADIANCE";
const magic_rgbe = "#?RGBE";

/// A decoded HDR image: linear RGB float triplets, row-major, top-to-bottom.
pub const HdrImage = struct {
    /// `width * height * 3` floats (R, G, B, ...).
    pixels: []f32,
    width: u32,
    height: u32,
    allocator: std.mem.Allocator,

    pub fn deinit(self: *@This()) void {
        self.allocator.free(self.pixels);
    }
};

/// True if `bytes` starts with a Radiance HDR magic line (`#?RADIANCE` / `#?RGBE`).
pub fn isHdr(bytes: []const u8) bool {
    return std.mem.startsWith(u8, bytes, magic_radiance) or std.mem.startsWith(u8, bytes, magic_rgbe);
}

fn rgbeToFloat(r: u8, g: u8, b: u8, e: u8, out: []f32) void {
    if (e == 0) {
        out[0] = 0;
        out[1] = 0;
        out[2] = 0;
        return;
    }
    const f = std.math.ldexp(@as(f32, 1.0), @as(i32, e) - (128 + 8));
    out[0] = @as(f32, @floatFromInt(r)) * f;
    out[1] = @as(f32, @floatFromInt(g)) * f;
    out[2] = @as(f32, @floatFromInt(b)) * f;
}

/// Reads one header text line (up to and excluding `\n`); returns the slice and
/// the offset just past the newline.
fn readLine(bytes: []const u8, start: usize) Error!struct { line: []const u8, next: usize } {
    var i = start;
    while (i < bytes.len and bytes[i] != '\n') : (i += 1) {}
    if (i >= bytes.len) return Error.Truncated;
    return .{ .line = bytes[start..i], .next = i + 1 };
}

const Header = struct { width: u32, height: u32, data_offset: usize };

/// Parses the text header (magic + variable lines + blank line) and the
/// resolution line (`-Y H +X W`, the only orientation this loader supports —
/// standard for HDRI environment maps).
fn parseHeader(bytes: []const u8) Error!Header {
    if (!isHdr(bytes)) return Error.NotHdr;

    var off: usize = 0;
    // Variable header lines until a blank line.
    while (true) {
        const r = try readLine(bytes, off);
        off = r.next;
        if (r.line.len == 0) break;
    }

    const res = try readLine(bytes, off);
    off = res.next;

    var it = std.mem.tokenizeScalar(u8, res.line, ' ');
    const y_sign = it.next() orelse return Error.UnsupportedFormat;
    const height_s = it.next() orelse return Error.UnsupportedFormat;
    const x_sign = it.next() orelse return Error.UnsupportedFormat;
    const width_s = it.next() orelse return Error.UnsupportedFormat;
    if (!std.mem.eql(u8, y_sign, "-Y") or !std.mem.eql(u8, x_sign, "+X"))
        return Error.UnsupportedFormat; // only top-down, left-to-right images

    const height = std.fmt.parseInt(u32, height_s, 10) catch return Error.UnsupportedFormat;
    const width = std.fmt.parseInt(u32, width_s, 10) catch return Error.UnsupportedFormat;
    if (width == 0 or height == 0) return Error.UnsupportedDimension;

    return .{ .width = width, .height = height, .data_offset = off };
}

/// Reads one scanline of `width` RGBE pixels (flat, legacy RLE, or new-style
/// per-channel RLE) into `scanline` (`width * 4` bytes: R,G,B,E per pixel).
/// Returns the number of input bytes consumed.
fn readScanline(bytes: []const u8, width: u32, scanline: []u8) Error!usize {
    if (bytes.len < 4) return Error.Truncated;

    const is_new_rle = width >= 8 and width < 0x8000 and
        bytes[0] == 2 and bytes[1] == 2 and
        (@as(u32, bytes[2]) << 8 | bytes[3]) == width;

    if (is_new_rle) {
        var pos: usize = 4;
        for (0..4) |channel| {
            var x: usize = 0;
            while (x < width) {
                if (pos >= bytes.len) return Error.Truncated;
                const c = bytes[pos];
                pos += 1;
                if (c > 128) {
                    const count = c - 128;
                    if (pos >= bytes.len or x + count > width) return Error.Truncated;
                    const v = bytes[pos];
                    pos += 1;
                    for (0..count) |i| scanline[(x + i) * 4 + channel] = v;
                    x += count;
                } else {
                    const count = c;
                    if (pos + count > bytes.len or x + count > width) return Error.Truncated;
                    for (0..count) |i| scanline[(x + i) * 4 + channel] = bytes[pos + i];
                    pos += count;
                    x += count;
                }
            }
        }
        return pos;
    }

    // Legacy format: flat RGBE, or old-style RLE where a pixel of (1,1,1,E)
    // means "repeat the previous pixel E times".
    var pos: usize = 0;
    var x: usize = 0;
    var prev: [4]u8 = .{ 0, 0, 0, 0 };
    while (x < width) {
        if (pos + 4 > bytes.len) return Error.Truncated;
        const px = bytes[pos..][0..4];
        pos += 4;
        if (px[0] == 1 and px[1] == 1 and px[2] == 1) {
            const count = @as(usize, px[3]);
            if (x + count > width) return Error.Truncated;
            for (0..count) |i| @memcpy(scanline[(x + i) * 4 ..][0..4], &prev);
            x += count;
        } else {
            @memcpy(scanline[x * 4 ..][0..4], px);
            prev = px.*;
            x += 1;
        }
    }
    return pos;
}

/// A decoded HDR image as flat RGBE quads (undoes the scanline RLE, keeps the
/// compact on-disk encoding) — the shape a cooked artifact stores, and what a
/// GPU upload path converts to float from just before creating the texture.
pub const RgbeImage = struct {
    /// `width * height * 4` bytes (R, G, B, E per pixel).
    pixels: []u8,
    width: u32,
    height: u32,
    allocator: std.mem.Allocator,

    pub fn deinit(self: *@This()) void {
        self.allocator.free(self.pixels);
    }
};

/// Decodes a Radiance HDR container into flat RGBE quads (scanline RLE undone,
/// pixel encoding kept). Caller owns the returned image; free with `deinit`.
pub fn decodeRgbe(allocator: std.mem.Allocator, bytes: []const u8) (Error || std.mem.Allocator.Error)!RgbeImage {
    const h = try parseHeader(bytes);

    const pixels = try allocator.alloc(u8, @as(usize, h.width) * h.height * 4);
    errdefer allocator.free(pixels);

    var off = h.data_offset;
    for (0..h.height) |row| {
        if (off >= bytes.len) return Error.Truncated;
        const consumed = try readScanline(bytes[off..], h.width, pixels[row * @as(usize, h.width) * 4 ..]);
        off += consumed;
    }

    return .{ .pixels = pixels, .width = h.width, .height = h.height, .allocator = allocator };
}

/// Decodes a Radiance HDR container into linear float RGB. Caller owns the
/// returned image; free with `deinit`.
pub fn decode(allocator: std.mem.Allocator, bytes: []const u8) (Error || std.mem.Allocator.Error)!HdrImage {
    var rgbe = try decodeRgbe(allocator, bytes);
    defer rgbe.deinit();

    const pixels = try allocator.alloc(f32, @as(usize, rgbe.width) * rgbe.height * 3);
    errdefer allocator.free(pixels);

    for (0..@as(usize, rgbe.width) * rgbe.height) |i| {
        rgbeToFloat(rgbe.pixels[i * 4], rgbe.pixels[i * 4 + 1], rgbe.pixels[i * 4 + 2], rgbe.pixels[i * 4 + 3], pixels[i * 3 ..][0..3]);
    }

    return .{ .pixels = pixels, .width = rgbe.width, .height = rgbe.height, .allocator = allocator };
}

// ── Cooked envelope (import-time artifact format) ───────────────────────────
// A cooked environment artifact is a tiny header plus flat RGBE quads — the
// scanline RLE is undone once at import (not re-encoded: the complexity isn't
// worth it for a one-time cook), while the RGBE byte encoding is kept so the
// artifact stays close to the source's size instead of ballooning 4x as float.

pub const envelope_magic = "ENVF";
const ENVELOPE_HEADER_LEN = 12; // magic(4) + width(4) + height(4)

/// True if `bytes` is a cooked environment envelope (see `encodeEnvelope`).
pub fn isEnvelope(bytes: []const u8) bool {
    return bytes.len >= ENVELOPE_HEADER_LEN and std.mem.eql(u8, bytes[0..4], envelope_magic);
}

/// Cooks a source `.hdr` file's bytes into the compact envelope artifact.
/// Caller owns the returned slice.
pub fn encodeEnvelope(allocator: std.mem.Allocator, hdr_bytes: []const u8) (Error || std.mem.Allocator.Error)![]u8 {
    var rgbe = try decodeRgbe(allocator, hdr_bytes);
    defer rgbe.deinit();

    const out = try allocator.alloc(u8, ENVELOPE_HEADER_LEN + rgbe.pixels.len);
    @memcpy(out[0..4], envelope_magic);
    std.mem.writeInt(u32, out[4..8], rgbe.width, .little);
    std.mem.writeInt(u32, out[8..12], rgbe.height, .little);
    @memcpy(out[ENVELOPE_HEADER_LEN..], rgbe.pixels);
    return out;
}

/// A view into cooked envelope bytes — no allocation, borrows `bytes`.
pub const EnvelopeView = struct {
    /// `width * height * 4` bytes (R, G, B, E per pixel), borrowed from the
    /// envelope bytes passed to `decodeEnvelope`.
    pixels: []const u8,
    width: u32,
    height: u32,
};

/// Parses a cooked envelope's header, returning a zero-copy view of its RGBE
/// pixel bytes.
pub fn decodeEnvelope(bytes: []const u8) Error!EnvelopeView {
    if (!isEnvelope(bytes)) return Error.NotEnvelope;
    const width = std.mem.readInt(u32, bytes[4..8], .little);
    const height = std.mem.readInt(u32, bytes[8..12], .little);
    const expected = @as(usize, width) * @as(usize, height) * 4;
    if (bytes.len < ENVELOPE_HEADER_LEN + expected) return Error.Truncated;
    return .{ .pixels = bytes[ENVELOPE_HEADER_LEN..][0..expected], .width = width, .height = height };
}

/// Decodes a cooked envelope's RGBE pixels into linear float RGB — the same
/// shape `decode` produces from a raw `.hdr` container, so a GPU upload path
/// can treat both sources uniformly. Caller owns the returned image.
pub fn decodeEnvelopeToImage(allocator: std.mem.Allocator, bytes: []const u8) (Error || std.mem.Allocator.Error)!HdrImage {
    const view = try decodeEnvelope(bytes);

    const pixels = try allocator.alloc(f32, @as(usize, view.width) * view.height * 3);
    errdefer allocator.free(pixels);

    for (0..@as(usize, view.width) * view.height) |i| {
        rgbeToFloat(view.pixels[i * 4], view.pixels[i * 4 + 1], view.pixels[i * 4 + 2], view.pixels[i * 4 + 3], pixels[i * 3 ..][0..3]);
    }

    return .{ .pixels = pixels, .width = view.width, .height = view.height, .allocator = allocator };
}

// ── Tests ─────────────────────────────────────────────────────────────────────

test "isHdr detects the Radiance magic and rejects other containers" {
    try std.testing.expect(isHdr("#?RADIANCE\nblah"));
    try std.testing.expect(isHdr("#?RGBE\nblah"));
    try std.testing.expect(!isHdr("not an hdr file"));
}

fn buildFlatFixture(allocator: std.mem.Allocator, width: u32, height: u32, pixel: [4]u8) ![]u8 {
    var list = std.ArrayList(u8).empty;
    errdefer list.deinit(allocator);
    try list.appendSlice(allocator, "#?RADIANCE\n");
    try list.appendSlice(allocator, "FORMAT=32-bit_rle_rgbe\n");
    try list.appendSlice(allocator, "\n");
    const res_line = try std.fmt.allocPrint(allocator, "-Y {d} +X {d}\n", .{ height, width });
    defer allocator.free(res_line);
    try list.appendSlice(allocator, res_line);
    for (0..height) |_| for (0..width) |_| try list.appendSlice(allocator, &pixel);
    return list.toOwnedSlice(allocator);
}

test "decode reads a flat (non-RLE) scanline image" {
    // Small width (<8) keeps the flat/legacy path (new-style RLE requires >= 8).
    const bytes = try buildFlatFixture(std.testing.allocator, 4, 2, .{ 128, 64, 32, 130 });
    defer std.testing.allocator.free(bytes);

    var img = try decode(std.testing.allocator, bytes);
    defer img.deinit();

    try std.testing.expectEqual(@as(u32, 4), img.width);
    try std.testing.expectEqual(@as(u32, 2), img.height);

    var expect: [3]f32 = undefined;
    rgbeToFloat(128, 64, 32, 130, &expect);
    try std.testing.expectApproxEqAbs(expect[0], img.pixels[0], 1e-6);
    try std.testing.expectApproxEqAbs(expect[1], img.pixels[1], 1e-6);
    try std.testing.expectApproxEqAbs(expect[2], img.pixels[2], 1e-6);
}

test "decode handles legacy RLE repeat pixels" {
    var list = std.ArrayList(u8).empty;
    defer list.deinit(std.testing.allocator);
    try list.appendSlice(std.testing.allocator, "#?RADIANCE\n\n-Y 1 +X 4\n");
    // One real pixel, then a repeat-run of 3 covering the rest of the 4-wide row.
    try list.appendSlice(std.testing.allocator, &[_]u8{ 10, 20, 30, 129 });
    try list.appendSlice(std.testing.allocator, &[_]u8{ 1, 1, 1, 3 });

    var img = try decode(std.testing.allocator, list.items);
    defer img.deinit();

    var expect: [3]f32 = undefined;
    rgbeToFloat(10, 20, 30, 129, &expect);
    for (0..4) |x| {
        try std.testing.expectApproxEqAbs(expect[0], img.pixels[x * 3], 1e-6);
        try std.testing.expectApproxEqAbs(expect[1], img.pixels[x * 3 + 1], 1e-6);
        try std.testing.expectApproxEqAbs(expect[2], img.pixels[x * 3 + 2], 1e-6);
    }
}

test "decode reads a new-style per-channel RLE scanline" {
    const width: u32 = 8;
    var list = std.ArrayList(u8).empty;
    defer list.deinit(std.testing.allocator);
    try list.appendSlice(std.testing.allocator, "#?RADIANCE\n\n-Y 1 +X 8\n");
    try list.appendSlice(std.testing.allocator, &[_]u8{ 2, 2, 0, 8 }); // new-RLE marker, width=8
    // R channel: a run of 8 identical values (200).
    try list.appendSlice(std.testing.allocator, &[_]u8{ 128 + 8, 200 });
    // G channel: a literal run of 8 values 0..7.
    try list.appendSlice(std.testing.allocator, &[_]u8{8});
    try list.appendSlice(std.testing.allocator, &[_]u8{ 0, 1, 2, 3, 4, 5, 6, 7 });
    // B channel: a run of 8 identical values (50).
    try list.appendSlice(std.testing.allocator, &[_]u8{ 128 + 8, 50 });
    // E channel: a run of 8 identical values (128).
    try list.appendSlice(std.testing.allocator, &[_]u8{ 128 + 8, 128 });

    var img = try decode(std.testing.allocator, list.items);
    defer img.deinit();

    try std.testing.expectEqual(width, img.width);
    for (0..8) |x| {
        var expect: [3]f32 = undefined;
        rgbeToFloat(200, @intCast(x), 50, 128, &expect);
        try std.testing.expectApproxEqAbs(expect[0], img.pixels[x * 3], 1e-6);
        try std.testing.expectApproxEqAbs(expect[1], img.pixels[x * 3 + 1], 1e-6);
        try std.testing.expectApproxEqAbs(expect[2], img.pixels[x * 3 + 2], 1e-6);
    }
}

test "decode rejects a non-HDR buffer" {
    try std.testing.expectError(Error.NotHdr, decode(std.testing.allocator, "not an hdr file"));
}

fn decodeFixture(path: []const u8) !HdrImage {
    const bytes = std.Io.Dir.cwd().readFileAlloc(std.testing.io, path, std.testing.allocator, .unlimited) catch
        return error.SkipZigTest;
    defer std.testing.allocator.free(bytes);
    return decode(std.testing.allocator, bytes);
}

test "decodes the real Bistro HDRI environment map" {
    const path = "/media/work/dev/mega4/turian-samples/bistro/assets/san_giuseppe_bridge_4k.hdr";
    var img = try decodeFixture(path);
    defer img.deinit();
    try std.testing.expectEqual(@as(u32, 4096), img.width);
    try std.testing.expectEqual(@as(u32, 2048), img.height);
    // Sanity: at least one pixel should carry real (non-zero) radiance.
    var any_nonzero = false;
    for (img.pixels) |p| {
        if (p > 0) {
            any_nonzero = true;
            break;
        }
    }
    try std.testing.expect(any_nonzero);
}

test "encodeEnvelope/decodeEnvelope round-trip flat RGBE pixels" {
    const bytes = try buildFlatFixture(std.testing.allocator, 4, 2, .{ 128, 64, 32, 130 });
    defer std.testing.allocator.free(bytes);

    const cooked = try encodeEnvelope(std.testing.allocator, bytes);
    defer std.testing.allocator.free(cooked);

    try std.testing.expect(isEnvelope(cooked));
    const view = try decodeEnvelope(cooked);
    try std.testing.expectEqual(@as(u32, 4), view.width);
    try std.testing.expectEqual(@as(u32, 2), view.height);

    var img = try decodeEnvelopeToImage(std.testing.allocator, cooked);
    defer img.deinit();
    var expect: [3]f32 = undefined;
    rgbeToFloat(128, 64, 32, 130, &expect);
    try std.testing.expectEqualSlices(f32, &expect, img.pixels[0..3]);
}

test "decodeEnvelope rejects bytes without the envelope magic" {
    try std.testing.expectError(Error.NotEnvelope, decodeEnvelope("not an envelope"));
}

test {
    std.testing.refAllDecls(@This());
}
