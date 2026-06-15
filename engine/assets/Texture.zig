const std = @import("std");
const ktx2 = @import("ktx2");

/// GPU pixel format of a texture (uncompressed RGBA8 or a block-compressed
/// format). Shared with the `ktx2` reader so no mapping is needed at the seam.
pub const Format = ktx2.Format;

/// One mip level: a byte range within `Texture.data` plus its dimensions.
pub const Mip = ktx2.Level;

/// A texture image owned by the caller. Holds either uncompressed RGBA8 pixels
/// or block-compressed data (BCn), optionally with multiple mip levels. Call
/// deinit() to free.
pub const Texture = struct {
    /// Raw pixel data: RGBA8 for uncompressed formats, or packed blocks for
    /// compressed ones. When `mips` is non-empty, it is all levels concatenated.
    data: []u8,
    /// Width of mip 0 in pixels.
    width: u32,
    /// Height of mip 0 in pixels.
    height: u32,
    /// Pixel format. Defaults to uncompressed RGBA8 (the stb_image path).
    format: Format = .rgba8_unorm,
    /// Mip levels (level 0 = largest). Empty means a single implicit level
    /// covering `data` at `width`×`height`.
    mips: []const Mip = &.{},
    /// Allocator used for `data` (and `mips` when `owns_mips`).
    allocator: std.mem.Allocator,
    /// Whether `mips` was heap-allocated and must be freed by `deinit`.
    owns_mips: bool = false,

    /// Frees the pixel data (and the mip table when owned).
    pub fn deinit(self: *@This()) void {
        self.allocator.free(self.data);
        if (self.owns_mips and self.mips.len > 0)
            self.allocator.free(@constCast(self.mips));
    }

    /// True when the format is block-compressed (BCn) rather than RGBA8.
    pub fn isCompressed(self: @This()) bool {
        return self.format.isCompressed();
    }
};
