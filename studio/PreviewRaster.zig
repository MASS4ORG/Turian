//! Pure CPU-side raster helpers for `PreviewSystem`: fitting a decoded image
//! into a square thumbnail, and turning PCM16 `.wav` samples into a waveform
//! image. No GPU/GUI/filesystem dependency, so this is unit-testable without
//! the full studio build graph.
const std = @import("std");

pub const Raster = struct { pixels: []u8, w: u32, h: u32 };

/// Box-resize (nearest-sample) `src` (RGBA8, `sw`×`sh`) to fit within a
/// `thumb_size` square, centered with transparent padding. Caller owns the
/// returned pixels.
pub fn resizeToThumb(allocator: std.mem.Allocator, src: []const u8, sw: u32, sh: u32, thumb_size: u32) ?Raster {
    if (sw == 0 or sh == 0) return null;
    const pixels = allocator.alloc(u8, @as(usize, thumb_size) * thumb_size * 4) catch return null;
    @memset(pixels, 0);

    const scale = @min(
        @as(f32, @floatFromInt(thumb_size)) / @as(f32, @floatFromInt(sw)),
        @as(f32, @floatFromInt(thumb_size)) / @as(f32, @floatFromInt(sh)),
    );
    const dw: u32 = @max(1, @as(u32, @intFromFloat(@as(f32, @floatFromInt(sw)) * scale)));
    const dh: u32 = @max(1, @as(u32, @intFromFloat(@as(f32, @floatFromInt(sh)) * scale)));
    const x0 = (thumb_size - dw) / 2;
    const y0 = (thumb_size - dh) / 2;

    for (0..dh) |dy| {
        const sy = @min(sh - 1, @as(u32, @intFromFloat(@as(f32, @floatFromInt(dy)) / scale)));
        for (0..dw) |dx| {
            const sx = @min(sw - 1, @as(u32, @intFromFloat(@as(f32, @floatFromInt(dx)) / scale)));
            const si = (sy * sw + sx) * 4;
            const di = ((y0 + dy) * thumb_size + (x0 + dx)) * 4;
            pixels[di + 0] = src[si + 0];
            pixels[di + 1] = src[si + 1];
            pixels[di + 2] = src[si + 2];
            pixels[di + 3] = src[si + 3];
        }
    }
    return .{ .pixels = pixels, .w = thumb_size, .h = thumb_size };
}

pub const WavInfo = struct { data: []const u8, channels: u16 };

/// Parse a minimal PCM16 `.wav`: RIFF/WAVE container, `fmt ` (tag=1, 16-bit)
/// and `data` chunks. Returns null for anything else (compressed, float,
/// non-16-bit, or not a RIFF/WAVE file at all) — callers fall back to the
/// type icon for formats this doesn't understand (no ogg/mp3 decoder here).
pub fn parseWav(bytes: []const u8) ?WavInfo {
    if (bytes.len < 12 or !std.mem.eql(u8, bytes[0..4], "RIFF") or !std.mem.eql(u8, bytes[8..12], "WAVE")) return null;

    var off: usize = 12;
    var channels: u16 = 0;
    var bits: u16 = 0;
    var fmt_tag: u16 = 0;
    var data: ?[]const u8 = null;
    while (off + 8 <= bytes.len) {
        const id = bytes[off..][0..4];
        const size = std.mem.readInt(u32, bytes[off + 4 ..][0..4], .little);
        const body = off + 8;
        if (body + size > bytes.len) break;
        if (std.mem.eql(u8, id, "fmt ") and size >= 16) {
            fmt_tag = std.mem.readInt(u16, bytes[body..][0..2], .little);
            channels = std.mem.readInt(u16, bytes[body + 2 ..][0..2], .little);
            bits = std.mem.readInt(u16, bytes[body + 14 ..][0..2], .little);
        } else if (std.mem.eql(u8, id, "data")) {
            data = bytes[body .. body + size];
        }
        off = body + size + (size & 1); // chunks are word-aligned
    }
    if (fmt_tag != 1 or bits != 16 or channels == 0) return null;
    return .{ .data = data orelse return null, .channels = channels };
}

/// Render `wav`'s peak-amplitude envelope as a `thumb_size` square strip
/// (dark background, one vertical bar per column, centered vertically).
pub fn waveformRaster(allocator: std.mem.Allocator, wav: WavInfo, thumb_size: u32) ?Raster {
    const pixels = allocator.alloc(u8, @as(usize, thumb_size) * thumb_size * 4) catch return null;
    var i: usize = 0;
    while (i < pixels.len) : (i += 4) {
        pixels[i] = 30;
        pixels[i + 1] = 30;
        pixels[i + 2] = 34;
        pixels[i + 3] = 255;
    }

    const stride = @as(usize, wav.channels) * 2;
    const frame_count = wav.data.len / stride;
    if (frame_count == 0) return .{ .pixels = pixels, .w = thumb_size, .h = thumb_size };

    const per_col = @max(frame_count / thumb_size, 1);
    const mid = thumb_size / 2;
    for (0..thumb_size) |cx| {
        const start = cx * per_col;
        if (start >= frame_count) break;
        const end = @min(start + per_col, frame_count);
        var peak: u32 = 0;
        var s = start;
        while (s < end) : (s += 1) {
            const off = s * stride;
            const v: i16 = @bitCast(bytes2(wav.data[off], wav.data[off + 1]));
            const a = @abs(@as(i32, v));
            if (a > peak) peak = a;
        }
        const norm = @as(f32, @floatFromInt(peak)) / 32768.0;
        const half_h: u32 = @intFromFloat(norm * @as(f32, @floatFromInt(mid -| 2)));
        const y0 = mid -| half_h;
        const y1 = @min(mid + half_h, thumb_size - 1);
        var y = y0;
        while (y <= y1) : (y += 1) {
            const idx = (y * thumb_size + cx) * 4;
            pixels[idx + 0] = 130;
            pixels[idx + 1] = 190;
            pixels[idx + 2] = 255;
            pixels[idx + 3] = 255;
        }
    }
    return .{ .pixels = pixels, .w = thumb_size, .h = thumb_size };
}

fn bytes2(lo: u8, hi: u8) u16 {
    return @as(u16, lo) | (@as(u16, hi) << 8);
}

test "resizeToThumb centers a non-square image inside the thumb square" {
    var src = [_]u8{ 255, 0, 0, 255, 0, 0, 255, 255 };
    const r = resizeToThumb(std.testing.allocator, &src, 2, 1, 128).?;
    defer std.testing.allocator.free(r.pixels);
    try std.testing.expectEqual(@as(u32, 128), r.w);
    try std.testing.expectEqual(@as(u32, 128), r.h);
    // Top row is still padding (transparent).
    try std.testing.expectEqual(@as(u8, 0), r.pixels[3]);
    // The scaled image should land in a horizontal band around the middle row.
    const mid_row_start = (64 * 128 + 32) * 4;
    try std.testing.expectEqual(@as(u8, 255), r.pixels[mid_row_start + 3]);
}

test "resizeToThumb rejects a zero-sized source" {
    var src = [_]u8{};
    try std.testing.expect(resizeToThumb(std.testing.allocator, &src, 0, 0, 128) == null);
}

test "parseWav rejects non-RIFF data" {
    try std.testing.expect(parseWav("not a wav") == null);
    try std.testing.expect(parseWav("") == null);
}

test "parseWav accepts a minimal PCM16 mono file" {
    var buf: [12 + 8 + 16 + 8 + 8]u8 = undefined;
    @memcpy(buf[0..4], "RIFF");
    std.mem.writeInt(u32, buf[4..8], @intCast(buf.len - 8), .little);
    @memcpy(buf[8..12], "WAVE");
    @memcpy(buf[12..16], "fmt ");
    std.mem.writeInt(u32, buf[16..20], 16, .little);
    std.mem.writeInt(u16, buf[20..22], 1, .little); // PCM
    std.mem.writeInt(u16, buf[22..24], 1, .little); // mono
    std.mem.writeInt(u32, buf[24..28], 44100, .little);
    std.mem.writeInt(u32, buf[28..32], 88200, .little);
    std.mem.writeInt(u16, buf[32..34], 2, .little);
    std.mem.writeInt(u16, buf[34..36], 16, .little);
    @memcpy(buf[36..40], "data");
    std.mem.writeInt(u32, buf[40..44], 8, .little);
    std.mem.writeInt(i16, buf[44..46], 1000, .little);
    std.mem.writeInt(i16, buf[46..48], -2000, .little);
    std.mem.writeInt(i16, buf[48..50], 3000, .little);
    std.mem.writeInt(i16, buf[50..52], -500, .little);

    const info = parseWav(&buf).?;
    try std.testing.expectEqual(@as(u16, 1), info.channels);
    try std.testing.expectEqual(@as(usize, 8), info.data.len);

    const r = waveformRaster(std.testing.allocator, info, 128).?;
    defer std.testing.allocator.free(r.pixels);
    try std.testing.expectEqual(@as(u32, 128), r.w);
}

test "parseWav rejects non-PCM16 formats" {
    var buf: [12 + 8 + 16]u8 = undefined;
    @memcpy(buf[0..4], "RIFF");
    std.mem.writeInt(u32, buf[4..8], @intCast(buf.len - 8), .little);
    @memcpy(buf[8..12], "WAVE");
    @memcpy(buf[12..16], "fmt ");
    std.mem.writeInt(u32, buf[16..20], 16, .little);
    std.mem.writeInt(u16, buf[20..22], 3, .little); // IEEE float, not PCM
    std.mem.writeInt(u16, buf[22..24], 1, .little);
    std.mem.writeInt(u32, buf[24..28], 44100, .little);
    std.mem.writeInt(u32, buf[28..32], 176400, .little);
    std.mem.writeInt(u16, buf[32..34], 4, .little);
    std.mem.writeInt(u16, buf[34..36], 32, .little);
    try std.testing.expect(parseWav(&buf) == null);
}

test "waveformRaster tolerates zero PCM frames" {
    const r = waveformRaster(std.testing.allocator, .{ .data = &.{}, .channels = 1 }, 32).?;
    defer std.testing.allocator.free(r.pixels);
    try std.testing.expectEqual(@as(u32, 32), r.w);
}
