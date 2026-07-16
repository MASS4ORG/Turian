//! ProjectSettings — game/project configuration as a DataAsset.
//!
//! Where the editor `Settings` store (editor/Settings.zig) holds *editor*
//! preferences, this asset holds *game/project* configuration that ships with the
//! built game: project metadata, graphics options, per-target platform options,
//! and which scene boots first. It is a single ZON asset (Unity-style — one
//! "Project Settings" window with several sections) rather than several files, so
//! there is exactly one place to edit and one place to consume.
//!
//! It is consumed in two places:
//!   * the **build** (`editor.GameBuild`) — selects the boot scene and bakes the
//!     window title / resolution into the generated game;
//!   * the **runtime** — the generated game boots into `first_scene` with the
//!     configured graphics options.
//!
//! File format (`.projectsettings`, ZON):
//! ```zon
//! .{
//!     .version = 1,
//!     .project = .{ .name = "My Game", .company = "Acme", .version = "1.0.0" },
//!     .graphics = .{ .width = 1280, .height = 720, .vsync = true, .quality = .high },
//!     .platform = .{ .target = .auto, .optimize = .debug },
//!     .first_scene = "00000000-0000-4000-8000-000000000000", // scene asset GUID
//! }
//! ```

const std = @import("std");
const Project = @import("../core/Project.zig").Project;

pub const ProjectSettings = struct {
    pub const CURRENT_VERSION: u32 = 1;

    /// Schema version; bump to trigger migration logic.
    version: u32 = CURRENT_VERSION,
    /// Project metadata (name, company, version, icon). Serialized form of the
    /// runtime `engine.Project` value — hydrate it with `toProject()`.
    project: Meta = .{},
    /// Graphics / display options consumed at game startup.
    graphics: Graphics = .{},
    /// Per-target build/runtime options consumed by the packaging step.
    platform: Platform = .{},
    /// GUID of the scene asset to load first at boot. Empty = let the build pick
    /// the conventional fallback scene.
    first_scene: []const u8 = "",
    /// GUID of a `.uitheme` asset the shipped game boots its UI with. Empty =
    /// keep today's behavior (OS light/dark preference, no override).
    ui_theme: []const u8 = "",

    /// Serialized project metadata. The runtime form is `engine.core.Project`
    /// (fixed buffers, no allocation); this is its editable/ZON counterpart.
    pub const Meta = struct {
        name: []const u8 = "Untitled",
        company: []const u8 = "",
        version: []const u8 = "0.1.0",
        /// GUID of an image asset used as the application/window icon (optional).
        icon: []const u8 = "",
    };

    /// Hydrate the runtime `engine.Project` value from this asset's metadata
    /// section. No allocation — copies into the runtime value's fixed buffers.
    pub fn toProject(self: ProjectSettings) Project {
        var p = Project{};
        p.setName(self.project.name);
        p.setCompany(self.project.company);
        p.setVersionString(self.project.version);
        p.setIcon(self.project.icon);
        return p;
    }

    /// Display and renderer options. Consumed by the game at startup.
    pub const Graphics = struct {
        width: u32 = 1280,
        height: u32 = 720,
        vsync: bool = true,
        fullscreen: bool = false,
        quality: Quality = .high,

        pub const Quality = enum { low, medium, high, ultra };
    };

    /// Per-target build/runtime options. Consumed by the packaging/build step.
    pub const Platform = struct {
        target: Target = .auto,
        optimize: Optimize = .debug,
        /// Where `editor.GameBuild` copies the built executable + packaged
        /// assets. Relative paths resolve against the project root; absolute
        /// paths are used as-is.
        build_output_path: []const u8 = ".public",

        /// Build target. `auto` = host platform.
        pub const Target = enum { auto, windows, linux, macos };
        /// Optimisation level passed through to the game build.
        pub const Optimize = enum { debug, release_safe, release_fast, release_small };
    };

    /// Parse a ProjectSettings asset from ZON bytes. Caller frees via `deinit`.
    pub fn loadFromBytes(allocator: std.mem.Allocator, bytes: []const u8) !ProjectSettings {
        const z = try allocator.dupeZ(u8, bytes);
        defer allocator.free(z);
        return std.zon.parse.fromSliceAlloc(ProjectSettings, allocator, z, null, .{});
    }

    /// Free slices owned by a ProjectSettings produced via `loadFromBytes`.
    pub fn deinit(self: ProjectSettings, allocator: std.mem.Allocator) void {
        // std.zon.parse.free crashes when a []const u8 field was not present in
        // the ZON source: the parser stores the default literal pointer directly,
        // and the allocator rejects freeing a read-only section address.
        // We compare each string pointer to its known default to skip literals.
        const d = ProjectSettings{};
        freeOwnedSlice(allocator, self.first_scene, d.first_scene);
        freeOwnedSlice(allocator, self.ui_theme, d.ui_theme);
        freeOwnedSlice(allocator, self.project.name, d.project.name);
        freeOwnedSlice(allocator, self.project.company, d.project.company);
        freeOwnedSlice(allocator, self.project.version, d.project.version);
        freeOwnedSlice(allocator, self.project.icon, d.project.icon);
        freeOwnedSlice(allocator, self.platform.build_output_path, d.platform.build_output_path);
    }

    fn freeOwnedSlice(allocator: std.mem.Allocator, s: []const u8, default: []const u8) void {
        if (s.ptr != default.ptr) allocator.free(s);
    }

    /// Serialize this asset as ZON into `writer`.
    pub fn serialize(self: ProjectSettings, writer: *std.Io.Writer) !void {
        try std.zon.stringify.serialize(self, .{}, writer);
    }

    /// Write this asset to `path` as a `.projectsettings` ZON file.
    pub fn save(self: ProjectSettings, io: std.Io, path: []const u8) !void {
        var buf: [1024 * 16]u8 = undefined;
        var writer = std.Io.Writer.fixed(&buf);
        try self.serialize(&writer);
        try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = writer.buffered() });
    }

    /// Map the optimization enum onto the `-Doptimize=` value the game build expects.
    pub fn optimizeFlag(self: ProjectSettings) []const u8 {
        return switch (self.platform.optimize) {
            .debug => "Debug",
            .release_safe => "ReleaseSafe",
            .release_fast => "ReleaseFast",
            .release_small => "ReleaseSmall",
        };
    }
};

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test "defaults are sane" {
    const ps = ProjectSettings{};
    try std.testing.expectEqual(@as(u32, 1280), ps.graphics.width);
    try std.testing.expectEqual(@as(u32, 720), ps.graphics.height);
    try std.testing.expect(ps.graphics.vsync);
    try std.testing.expectEqual(ProjectSettings.Graphics.Quality.high, ps.graphics.quality);
    try std.testing.expectEqualStrings("Debug", ps.optimizeFlag());
    try std.testing.expectEqualStrings(".public", ps.platform.build_output_path);
}

test "serialize then load round-trips settings" {
    const a = std.testing.allocator;
    const original = ProjectSettings{
        .version = 1,
        .project = .{ .name = "My Game", .company = "Acme", .version = "1.2.3" },
        .graphics = .{ .width = 1920, .height = 1080, .vsync = false, .quality = .ultra },
        .platform = .{ .target = .linux, .optimize = .release_fast },
        .first_scene = "58a6a0db-f0e9-4d4c-b7b4-7ff001b81fd7",
        .ui_theme = "00000000-0000-4000-8000-000000000300",
    };

    var buf: [4096]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buf);
    try original.serialize(&writer);

    var loaded = try ProjectSettings.loadFromBytes(a, writer.buffered());
    defer loaded.deinit(a);

    try std.testing.expectEqualStrings("My Game", loaded.project.name);
    try std.testing.expectEqualStrings("Acme", loaded.project.company);
    try std.testing.expectEqual(@as(u32, 1920), loaded.graphics.width);
    try std.testing.expect(!loaded.graphics.vsync);
    try std.testing.expectEqual(ProjectSettings.Graphics.Quality.ultra, loaded.graphics.quality);
    try std.testing.expectEqual(ProjectSettings.Platform.Target.linux, loaded.platform.target);
    try std.testing.expectEqualStrings("ReleaseFast", loaded.optimizeFlag());
    try std.testing.expectEqualStrings("58a6a0db-f0e9-4d4c-b7b4-7ff001b81fd7", loaded.first_scene);
    try std.testing.expectEqualStrings("00000000-0000-4000-8000-000000000300", loaded.ui_theme);
}

test "toProject hydrates the runtime Project value" {
    const ps = ProjectSettings{
        .project = .{ .name = "My Game", .company = "Acme", .version = "2.3.4", .icon = "abc" },
    };
    const p = ps.toProject();
    try std.testing.expectEqualStrings("My Game", p.nameSlice());
    try std.testing.expectEqualStrings("Acme", p.companySlice());
    try std.testing.expectEqual(@as(u32, 2), p.major);
    try std.testing.expectEqual(@as(u32, 3), p.minor);
    try std.testing.expectEqual(@as(u32, 4), p.patch);
    try std.testing.expectEqualStrings("abc", p.iconSlice());
}

test "unknown fields are ignored for forward compatibility" {
    const a = std.testing.allocator;
    // A future schema may add fields; older builds must still parse what they know.
    const sample =
        \\.{ .version = 1, .graphics = .{ .width = 800, .height = 600 } }
    ;
    var ps = try ProjectSettings.loadFromBytes(a, sample);
    defer ps.deinit(a);
    try std.testing.expectEqual(@as(u32, 800), ps.graphics.width);
    try std.testing.expectEqual(@as(u32, 600), ps.graphics.height);
}
