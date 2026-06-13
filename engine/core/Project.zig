const std = @import("std");

/// Maximum length of a project name.
pub const MAX_PROJECT_NAME = 128;
/// Maximum length of the company / publisher name.
pub const MAX_COMPANY_NAME = 128;
/// Length of a UUID string (the icon asset reference).
pub const GUID_LEN = 36;

/// Project metadata. This is the runtime value type (fixed buffers, no
/// allocation). The editable, serialized form is the `ProjectSettings` DataAsset
/// (`engine.assets.ProjectSettings`), which hydrates this via `toProject()`.
pub const Project = struct {
    name_buf: [MAX_PROJECT_NAME]u8 = std.mem.zeroes([MAX_PROJECT_NAME]u8),
    name_len: usize = 0,
    /// Company / publisher name.
    company_buf: [MAX_COMPANY_NAME]u8 = std.mem.zeroes([MAX_COMPANY_NAME]u8),
    company_len: usize = 0,
    /// Major version number.
    major: u32 = 0,
    /// Minor version number.
    minor: u32 = 1,
    /// Patch version number.
    patch: u32 = 0,
    /// GUID (UUID string) of the application/window icon image asset; empty = none.
    icon_buf: [GUID_LEN]u8 = .{0} ** GUID_LEN,
    icon_len: usize = 0,

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

    /// Returns the company name as a slice.
    pub fn companySlice(self: *const Project) []const u8 {
        return self.company_buf[0..self.company_len];
    }

    /// Sets the company name, truncating if necessary.
    pub fn setCompany(self: *Project, n: []const u8) void {
        const len = @min(n.len, MAX_COMPANY_NAME);
        @memcpy(self.company_buf[0..len], n[0..len]);
        self.company_len = len;
    }

    /// Returns the icon asset GUID, or empty if unset.
    pub fn iconSlice(self: *const Project) []const u8 {
        return self.icon_buf[0..self.icon_len];
    }

    /// Stores the icon asset GUID (UUID string).
    pub fn setIcon(self: *Project, s: []const u8) void {
        const len = @min(s.len, GUID_LEN);
        @memcpy(self.icon_buf[0..len], s[0..len]);
        self.icon_len = len;
    }

    /// Parse a "major.minor.patch" string into the version fields. Missing or
    /// malformed components are left at their current value.
    pub fn setVersionString(self: *Project, s: []const u8) void {
        var it = std.mem.splitScalar(u8, s, '.');
        if (it.next()) |m| self.major = std.fmt.parseInt(u32, m, 10) catch self.major;
        if (it.next()) |m| self.minor = std.fmt.parseInt(u32, m, 10) catch self.minor;
        if (it.next()) |m| self.patch = std.fmt.parseInt(u32, m, 10) catch self.patch;
    }

    /// Formats the version into `buf` as "major.minor.patch".
    pub fn versionString(self: *const Project, buf: []u8) []const u8 {
        return std.fmt.bufPrint(buf, "{d}.{d}.{d}", .{ self.major, self.minor, self.patch }) catch buf[0..0];
    }
};

test "version string round-trips" {
    var p = Project{};
    p.setVersionString("3.4.5");
    try std.testing.expectEqual(@as(u32, 3), p.major);
    try std.testing.expectEqual(@as(u32, 4), p.minor);
    try std.testing.expectEqual(@as(u32, 5), p.patch);
    var buf: [16]u8 = undefined;
    try std.testing.expectEqualStrings("3.4.5", p.versionString(&buf));
}
