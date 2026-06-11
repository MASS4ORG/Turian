const std = @import("std");

/// Maximum length of a project name.
pub const MAX_PROJECT_NAME = 128;

/// Project metadata read from project.json.
pub const Project = struct {
    name_buf: [MAX_PROJECT_NAME]u8 = std.mem.zeroes([MAX_PROJECT_NAME]u8),
    name_len: usize = 0,
    /// Major version number.
    major: u32 = 0,
    /// Minor version number.
    minor: u32 = 1,
    /// Patch version number.
    patch: u32 = 0,

    /// Returns the project name as a slice.
    pub fn nameSlice(self: *const Project) []const u8 {
        return self.name_buf[0..self.name_len];
    }

    /// Sets the project name, truncating if necessary.
    pub fn setName(self: *Project, n: []const u8) void {
        const len = @min(n.len, MAX_PROJECT_NAME);
        @memcpy(self.name_buf[0..len], n[0..len]);
        self.name_len = len;
    }
};
