/// Package discovery, manifest loading, and dependency graph.
/// Scans `<project>/packages/` for installed packages and exposes
/// typed accessors consumed by the build pipeline (#57–#60).
const std = @import("std");
const manifest_mod = @import("PackageManifest.zig");
const PackageManifest = manifest_mod.PackageManifest;
const PackageType = manifest_mod.PackageType;
const ModuleEntry = manifest_mod.ModuleEntry;
const NativeEntry = manifest_mod.NativeEntry;
const PluginEntry = manifest_mod.PluginEntry;
const ProjectConfig = @import("../project/ProjectConfig.zig").ProjectConfig;
const package_store = @import("PackageStore.zig");

const PACKAGES_DIR = "packages";
const MANIFEST_FILE = "turian-package.json";

// ── Diagnostic ────────────────────────────────────────────────────────────────

pub const Diagnostic = struct {
    message: []const u8,
    is_error: bool,
};

// ── InstalledPackage ──────────────────────────────────────────────────────────

pub const InstalledPackage = struct {
    /// Absolute path to the package root directory.
    root: []const u8,
    /// Parsed manifest. Owned.
    manifest: PackageManifest,
};

// ── PackageManager ────────────────────────────────────────────────────────────

/// In-memory list of discovered packages for a project.
/// Create with `discover`; free with `deinit`.
pub const PackageManager = struct {
    allocator: std.mem.Allocator,
    packages: std.ArrayList(InstalledPackage),
    diagnostics: std.ArrayList(Diagnostic),

    /// Discover all installed packages for a project. Sources, in precedence
    /// order (earlier wins on name clash):
    ///   1. project-local vendored dir `<project_path>/packages/*`
    ///   2. the central store: each `project.json` `packages` entry resolved to
    ///      `<store_root>/<name>/<version>` (skipped when `store_root` is empty)
    /// Each manifest is validated against `engine_version` (skipped at 0.0.0).
    /// Diagnostics (warnings and errors) accumulate in `self.diagnostics`.
    pub fn discover(
        io: std.Io,
        allocator: std.mem.Allocator,
        project_path: []const u8,
        engine_version: std.SemanticVersion,
        store_root: []const u8,
    ) PackageManager {
        var self = PackageManager{
            .allocator = allocator,
            .packages = .empty,
            .diagnostics = .empty,
        };
        self.scanPackagesDir(io, project_path);
        if (store_root.len > 0) self.scanStorePackages(io, project_path, store_root);
        self.validateCollisions();
        self.validateEngineCompat(engine_version);
        return self;
    }

    /// Parse an engine version string (e.g. "1.8.0") into a SemanticVersion,
    /// returning 0.0.0 (which disables the compat check) on failure.
    pub fn parseEngineVersion(s: []const u8) std.SemanticVersion {
        return std.SemanticVersion.parse(s) catch .{ .major = 0, .minor = 0, .patch = 0 };
    }

    pub fn deinit(self: *PackageManager) void {
        for (self.packages.items) |*pkg| {
            self.allocator.free(pkg.root);
            pkg.manifest.deinit();
        }
        self.packages.deinit(self.allocator);
        for (self.diagnostics.items) |d| self.allocator.free(d.message);
        self.diagnostics.deinit(self.allocator);
    }

    // ── Accessors ─────────────────────────────────────────────────────────────

    /// Returns a list of absolute asset-directory paths from all installed
    /// packages, allocated with `allocator`. Caller owns the returned slice
    /// (and each string inside it).
    pub fn assetRoots(self: *const PackageManager, allocator: std.mem.Allocator) ![][]const u8 {
        var roots: std.ArrayList([]const u8) = .empty;
        errdefer {
            for (roots.items) |r| allocator.free(r);
            roots.deinit(allocator);
        }
        for (self.packages.items) |*pkg| {
            for (pkg.manifest.asset_dirs) |dir| {
                const abs = try std.fmt.allocPrint(allocator, "{s}/{s}", .{ pkg.root, dir });
                try roots.append(allocator, abs);
            }
        }
        return roots.toOwnedSlice(allocator);
    }

    /// A source module exported by a package, with the package root needed to
    /// resolve `module.root` to an absolute path. Strings borrow from the
    /// package's manifest — valid for the PackageManager's lifetime.
    pub const SourceModule = struct {
        package: []const u8,
        root: []const u8,
        module: ModuleEntry,
    };
    /// A native library exported by a package.
    pub const NativeLib = struct {
        package: []const u8,
        root: []const u8,
        native: NativeEntry,
    };
    /// A plugin entry exported by a package.
    pub const Plugin = struct {
        package: []const u8,
        plugin: PluginEntry,
    };

    /// Source modules from every `source` package, for the generated-build seam
    /// (#61). Caller owns the returned slice; strings borrow from manifests.
    pub fn sourceModules(self: *const PackageManager, allocator: std.mem.Allocator) ![]SourceModule {
        var list: std.ArrayList(SourceModule) = .empty;
        errdefer list.deinit(allocator);
        for (self.packages.items) |*pkg| {
            for (pkg.manifest.modules) |m| {
                try list.append(allocator, .{ .package = pkg.manifest.name, .root = pkg.root, .module = m });
            }
        }
        return list.toOwnedSlice(allocator);
    }

    /// Native libraries from every `native` package, for the link seam (#62).
    pub fn nativeLibs(self: *const PackageManager, allocator: std.mem.Allocator) ![]NativeLib {
        var list: std.ArrayList(NativeLib) = .empty;
        errdefer list.deinit(allocator);
        for (self.packages.items) |*pkg| {
            for (pkg.manifest.native) |n| {
                try list.append(allocator, .{ .package = pkg.manifest.name, .root = pkg.root, .native = n });
            }
        }
        return list.toOwnedSlice(allocator);
    }

    /// Plugin entry points from every package that declares one, for runtime
    /// registration (#64). Caller owns the returned slice.
    pub fn plugins(self: *const PackageManager, allocator: std.mem.Allocator) ![]Plugin {
        var list: std.ArrayList(Plugin) = .empty;
        errdefer list.deinit(allocator);
        for (self.packages.items) |*pkg| {
            if (pkg.manifest.plugin) |p| {
                try list.append(allocator, .{ .package = pkg.manifest.name, .plugin = p });
            }
        }
        return list.toOwnedSlice(allocator);
    }

    /// Returns true if any discovered package reported an error diagnostic.
    pub fn hasErrors(self: *const PackageManager) bool {
        for (self.diagnostics.items) |d| if (d.is_error) return true;
        return false;
    }

    pub fn packageCount(self: *const PackageManager) usize {
        return self.packages.items.len;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    fn scanPackagesDir(self: *PackageManager, io: std.Io, project_path: []const u8) void {
        var buf: [1024]u8 = undefined;
        const packages_path = std.fmt.bufPrint(&buf, "{s}/{s}", .{ project_path, PACKAGES_DIR }) catch return;

        var dir = std.Io.Dir.cwd().openDir(io, packages_path, .{ .iterate = true }) catch return;
        defer dir.close(io);

        var iter = dir.iterate();
        while (iter.next(io) catch null) |entry| {
            if (entry.kind != .directory) continue;
            var pbuf: [1024]u8 = undefined;
            const pkg_path = std.fmt.bufPrint(&pbuf, "{s}/{s}", .{ packages_path, entry.name }) catch continue;
            self.loadPackage(io, pkg_path);
        }
    }

    /// Resolve the project's `project.json` `packages` to the central store and
    /// load each one. Vendored packages (already loaded) take precedence, so a
    /// store entry whose name is already present is skipped.
    fn scanStorePackages(self: *PackageManager, io: std.Io, project_path: []const u8, store_root: []const u8) void {
        var cfg = ProjectConfig.load(io, self.allocator, project_path) catch return;
        defer cfg.deinit();
        for (cfg.packages) |p| {
            if (self.hasPackageNamed(p.name)) continue;
            const dir = package_store.packagePath(self.allocator, store_root, p.name, p.version) catch continue;
            defer self.allocator.free(dir);
            self.loadPackage(io, dir);
        }
    }

    fn hasPackageNamed(self: *const PackageManager, name: []const u8) bool {
        for (self.packages.items) |*pkg| {
            if (std.mem.eql(u8, pkg.manifest.name, name)) return true;
        }
        return false;
    }

    fn loadPackage(self: *PackageManager, io: std.Io, pkg_path: []const u8) void {
        var mbuf: [1024]u8 = undefined;
        const manifest_path = std.fmt.bufPrint(&mbuf, "{s}/{s}", .{ pkg_path, MANIFEST_FILE }) catch return;

        const bytes = std.Io.Dir.cwd().readFileAlloc(io, manifest_path, self.allocator, .limited(64 * 1024)) catch {
            self.addDiag(true, "package at {s}: cannot read turian-package.json", .{pkg_path});
            return;
        };
        defer self.allocator.free(bytes);

        const manifest = PackageManifest.parse(self.allocator, bytes) catch |err| {
            self.addDiag(true, "package at {s}: manifest parse error: {s}", .{ pkg_path, @errorName(err) });
            return;
        };

        const root = self.allocator.dupe(u8, pkg_path) catch {
            manifest.deinit();
            return;
        };
        self.packages.append(self.allocator, .{ .root = root, .manifest = manifest }) catch {
            self.allocator.free(root);
            manifest.deinit();
        };
    }

    fn validateCollisions(self: *PackageManager) void {
        const pkgs = self.packages.items;
        for (pkgs, 0..) |*a, i| {
            for (pkgs[i + 1 ..]) |*b| {
                if (std.mem.eql(u8, a.manifest.name, b.manifest.name)) {
                    self.addDiag(true, "duplicate package name '{s}' found at '{s}' and '{s}'", .{
                        a.manifest.name, a.root, b.root,
                    });
                }
            }
        }
    }

    /// Validate each package's `engine_compat` range against `engine_version`.
    /// Incompatible packages produce a *warning* (non-fatal, per ADR-0001).
    /// Skipped entirely when `engine_version` is 0.0.0.
    fn validateEngineCompat(self: *PackageManager, engine_version: std.SemanticVersion) void {
        if (engine_version.major == 0 and engine_version.minor == 0 and engine_version.patch == 0) return;
        for (self.packages.items) |*pkg| {
            const compat = pkg.manifest.engine_compat;
            if (compat.len == 0) continue;
            if (!checkEngineCompat(compat, engine_version)) {
                self.addDiag(false, "package '{s}' requires engine {s} (running {d}.{d}.{d})", .{
                    pkg.manifest.name, compat, engine_version.major, engine_version.minor, engine_version.patch,
                });
            }
        }
    }

    fn addDiag(self: *PackageManager, is_error: bool, comptime fmt: []const u8, args: anytype) void {
        const msg = std.fmt.allocPrint(self.allocator, fmt, args) catch return;
        self.diagnostics.append(self.allocator, .{ .message = msg, .is_error = is_error }) catch {
            self.allocator.free(msg);
        };
    }
};

// ── Engine-compat check ───────────────────────────────────────────────────────

/// Check whether `engine_compat` (e.g. ">=1.0.0") is satisfied by the
/// current SDK version. Returns true when the compat string is empty/absent
/// (compatibility assumed) or when the range is satisfied.
pub fn checkEngineCompat(compat_range: []const u8, engine_version: std.SemanticVersion) bool {
    if (compat_range.len == 0) return true;
    var rest = compat_range;
    while (rest.len > 0) {
        rest = std.mem.trimStart(u8, rest, " ");
        if (rest.len == 0) break;
        const op_len: usize, const op: enum { gte, gt, lte, lt, caret } = blk: {
            if (std.mem.startsWith(u8, rest, ">=")) break :blk .{ 2, .gte };
            if (std.mem.startsWith(u8, rest, ">")) break :blk .{ 1, .gt };
            if (std.mem.startsWith(u8, rest, "<=")) break :blk .{ 2, .lte };
            if (std.mem.startsWith(u8, rest, "<")) break :blk .{ 1, .lt };
            if (std.mem.startsWith(u8, rest, "^")) break :blk .{ 1, .caret };
            return true; // unknown operator — assume compatible
        };
        rest = rest[op_len..];
        const ver_end = std.mem.indexOfAny(u8, rest, " ,") orelse rest.len;
        const ver_str = rest[0..ver_end];
        rest = rest[ver_end..];
        const ver = std.SemanticVersion.parse(ver_str) catch return true;
        const satisfied = switch (op) {
            .gte => engine_version.order(ver) != .lt,
            .gt => engine_version.order(ver) == .gt,
            .lte => engine_version.order(ver) != .gt,
            .lt => engine_version.order(ver) == .lt,
            .caret => engine_version.major == ver.major and engine_version.order(ver) != .lt,
        };
        if (!satisfied) return false;
    }
    return true;
}

// ── tests ─────────────────────────────────────────────────────────────────────

test "engine compat: no constraint passes" {
    const v = std.SemanticVersion{ .major = 1, .minor = 8, .patch = 0 };
    try std.testing.expect(checkEngineCompat("", v));
}

test "engine compat: >=1.0.0 passes for 1.8.0" {
    const v = std.SemanticVersion{ .major = 1, .minor = 8, .patch = 0 };
    try std.testing.expect(checkEngineCompat(">=1.0.0", v));
}

test "engine compat: >=2.0.0 fails for 1.8.0" {
    const v = std.SemanticVersion{ .major = 1, .minor = 8, .patch = 0 };
    try std.testing.expect(!checkEngineCompat(">=2.0.0", v));
}

test "engine compat: ^1.0.0 passes for 1.8.0" {
    const v = std.SemanticVersion{ .major = 1, .minor = 8, .patch = 0 };
    try std.testing.expect(checkEngineCompat("^1.0.0", v));
}

test "engine compat: ^2.0.0 fails for 1.8.0" {
    const v = std.SemanticVersion{ .major = 1, .minor = 8, .patch = 0 };
    try std.testing.expect(!checkEngineCompat("^2.0.0", v));
}
