/// Provider-agnostic release types.
/// Implement a new file (e.g. github.zig) next to gitlab.zig to add another
/// CI provider; it must expose the same `run(io, gpa, environ)` entry point.
const std = @import("std");

/// Target platform for release packaging.
pub const Platform = enum {
    linux_x86_64,
    windows_x86_64,
    macos_x86_64,
    macos_aarch64,

    /// Parse a platform from a string identifier.
    pub fn fromString(s: []const u8) ?Platform {
        if (std.mem.eql(u8, s, "linux-x86_64")) return .linux_x86_64;
        if (std.mem.eql(u8, s, "windows-x86_64")) return .windows_x86_64;
        if (std.mem.eql(u8, s, "macos-x86_64")) return .macos_x86_64;
        if (std.mem.eql(u8, s, "macos-aarch64")) return .macos_aarch64;
        return null;
    }

    /// Return a human-readable label for this platform.
    pub fn label(self: Platform) []const u8 {
        return switch (self) {
            .linux_x86_64 => "Linux x86_64 — studio + cli",
            .windows_x86_64 => "Windows x86_64 — cli",
            .macos_x86_64 => "macOS x86_64 Intel — cli",
            .macos_aarch64 => "macOS aarch64 Apple Silicon — cli",
        };
    }

    /// Infer platform from a .public/ filename such as "turian-linux-x86_64-v0.2.0.tar.gz".
    /// Infer the platform from a .public/ filename.
    pub fn fromFileName(name: []const u8) Platform {
        if (std.mem.indexOf(u8, name, "windows") != null) return .windows_x86_64;
        if (std.mem.indexOf(u8, name, "macos-aarch64") != null) return .macos_aarch64;
        if (std.mem.indexOf(u8, name, "macos-x86_64") != null) return .macos_x86_64;
        return .linux_x86_64;
    }
};

/// Represents a release artifact file with metadata.
pub const Artifact = struct {
    label: []const u8,
    local_path: []const u8,
    file_name: []const u8,
    download_url: []const u8 = "",
};

/// Metadata for a complete release including version and artifacts.
pub const ReleaseInfo = struct {
    tag_name: []const u8,
    version: []const u8, // tag without leading 'v'
    description: []const u8,
    artifacts: []Artifact,
};
