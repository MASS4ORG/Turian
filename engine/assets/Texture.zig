const std = @import("std");

/// RGBA8 image owned by the caller. Call deinit() to free.
pub const Texture = struct {
    /// Raw RGBA8 pixel data.
    data: []u8,
    /// Image width in pixels.
    width: u32,
    /// Image height in pixels.
    height: u32,
    /// Allocator used for the pixel data.
    allocator: std.mem.Allocator,

    /// Frees the pixel data.
    pub fn deinit(self: *@This()) void {
        self.allocator.free(self.data);
    }
};
