/// Turian-owned project configuration (`project.json`).
///
/// `project.json` is the **single source of truth** for a project's identity and
/// its declared dependencies. The project's `build.zig.zon` is a *generated
/// artifact* derived from this file (see `toBuildZon`) — like Unity/Flax
/// regenerate their `.csproj`/`.sln`. Users never hand-edit `build.zig.zon`,
/// so they cannot introduce ZON syntax bugs; the CLI/Studio mutate the JSON
/// (easy and safe) and regenerate the ZON deterministically.
///
/// Schema (all fields optional except `turian_version`, for backward
/// compatibility with the original one-line sentinel):
/// ```json
/// {
///   "turian_version": "0.16",
///   "name": "My Game",
///   "version": "0.0.0",
///   "dependencies": {
///     "some_dep":  { "url": "git+https://example.com/pkg#<ref>", "hash": "..." },
///     "local_dep": { "path": "../local-pkg" }
///   }
/// }
/// ```
const std = @import("std");

pub const PROJECT_FILE = "project.json";
const DEFAULT_TURIAN_VERSION = "0.16";
const MIN_ZIG_VERSION = "0.16.0";

/// One declared Zig **code** dependency (source/native package). Either a remote
/// `url` (+`hash`) resolved by Zig's package manager, or a local `path`. These
/// are emitted into the generated `build.zig.zon`.
pub const Dependency = struct {
    name: []const u8,
    url: []const u8 = "",
    hash: []const u8 = "",
    path: []const u8 = "",
};

/// One installed Turian **package** (asset/hybrid), resolved from the central
/// package store by `<name>/<version>`. `source` records where it was installed
/// from (a git URL or a local path) so it can be re-fetched/updated. These are
/// NOT emitted into `build.zig.zon` (asset-only packages have no `build.zig`).
pub const StorePackage = struct {
    name: []const u8,
    version: []const u8,
    source: []const u8 = "",
    /// True when `source` is a git URL (vs. a local filesystem path).
    is_git: bool = false,
};

pub const ProjectConfig = struct {
    allocator: std.mem.Allocator,
    turian_version: []const u8,
    name: []const u8,
    version: []const u8,
    dependencies: []Dependency,
    packages: []StorePackage,

    pub fn deinit(self: ProjectConfig) void {
        const a = self.allocator;
        a.free(self.turian_version);
        a.free(self.name);
        a.free(self.version);
        for (self.dependencies) |d| {
            a.free(d.name);
            a.free(d.url);
            a.free(d.hash);
            a.free(d.path);
        }
        a.free(self.dependencies);
        for (self.packages) |p| {
            a.free(p.name);
            a.free(p.version);
            a.free(p.source);
        }
        a.free(self.packages);
    }

    /// An empty config with sensible defaults. `name` is taken verbatim.
    pub fn initDefault(allocator: std.mem.Allocator, name: []const u8) !ProjectConfig {
        return .{
            .allocator = allocator,
            .turian_version = try allocator.dupe(u8, DEFAULT_TURIAN_VERSION),
            .name = try allocator.dupe(u8, name),
            .version = try allocator.dupe(u8, "0.0.0"),
            .dependencies = try allocator.alloc(Dependency, 0),
            .packages = try allocator.alloc(StorePackage, 0),
        };
    }

    /// Parse a `project.json` byte buffer. Tolerant of the legacy sentinel
    /// (`{"turian_version":"0.16"}`) — missing fields fall back to defaults.
    pub fn parse(allocator: std.mem.Allocator, json_bytes: []const u8) !ProjectConfig {
        const parsed = std.json.parseFromSlice(std.json.Value, allocator, json_bytes, .{}) catch
            return error.InvalidProjectJson;
        defer parsed.deinit();
        const obj = if (parsed.value == .object) parsed.value.object else return error.InvalidProjectJson;

        const turian_version = try dupeStr(allocator, getStr(obj, "turian_version") orelse DEFAULT_TURIAN_VERSION);
        errdefer allocator.free(turian_version);
        const name = try dupeStr(allocator, getStr(obj, "name") orelse "");
        errdefer allocator.free(name);
        const version = try dupeStr(allocator, getStr(obj, "version") orelse "0.0.0");
        errdefer allocator.free(version);

        const dependencies = try parseDeps(allocator, obj.get("dependencies"));
        errdefer freeDeps(allocator, dependencies);
        const packages = try parsePackages(allocator, obj.get("packages"));
        return .{
            .allocator = allocator,
            .turian_version = turian_version,
            .name = name,
            .version = version,
            .dependencies = dependencies,
            .packages = packages,
        };
    }

    /// Add or replace an installed store package (by name) and persist to
    /// `<project_path>/project.json`. Returns the resolved version stored.
    pub fn addPackage(
        self: *ProjectConfig,
        io: std.Io,
        project_path: []const u8,
        name: []const u8,
        version: []const u8,
        source: []const u8,
        is_git: bool,
    ) !void {
        const a = self.allocator;
        // Replace in place if the package is already recorded.
        for (self.packages) |*p| {
            if (std.mem.eql(u8, p.name, name)) {
                a.free(p.version);
                a.free(p.source);
                p.version = try a.dupe(u8, version);
                p.source = try a.dupe(u8, source);
                p.is_git = is_git;
                return self.save(io, project_path);
            }
        }
        var list = try a.alloc(StorePackage, self.packages.len + 1);
        @memcpy(list[0..self.packages.len], self.packages);
        list[self.packages.len] = .{
            .name = try a.dupe(u8, name),
            .version = try a.dupe(u8, version),
            .source = try a.dupe(u8, source),
            .is_git = is_git,
        };
        a.free(self.packages);
        self.packages = list;
        return self.save(io, project_path);
    }

    /// Remove an installed store package (by name) and persist. Returns true if
    /// a package was removed.
    pub fn removePackage(self: *ProjectConfig, io: std.Io, project_path: []const u8, name: []const u8) !bool {
        const a = self.allocator;
        for (self.packages, 0..) |p, i| {
            if (!std.mem.eql(u8, p.name, name)) continue;
            a.free(p.name);
            a.free(p.version);
            a.free(p.source);
            var list = try a.alloc(StorePackage, self.packages.len - 1);
            @memcpy(list[0..i], self.packages[0..i]);
            @memcpy(list[i..], self.packages[i + 1 ..]);
            a.free(self.packages);
            self.packages = list;
            try self.save(io, project_path);
            return true;
        }
        return false;
    }

    /// Write the config back to `<project_path>/project.json`.
    pub fn save(self: ProjectConfig, io: std.Io, project_path: []const u8) !void {
        const json = try self.toJson(self.allocator);
        defer self.allocator.free(json);
        var buf: [1024]u8 = undefined;
        const path = std.fmt.bufPrint(&buf, "{s}/{s}", .{ project_path, PROJECT_FILE }) catch
            return error.PathTooLong;
        try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = json });
    }

    /// Load `<project_path>/project.json`. Returns a default config if the file
    /// is missing or unreadable, so callers always get a usable value.
    pub fn load(io: std.Io, allocator: std.mem.Allocator, project_path: []const u8) !ProjectConfig {
        var buf: [1024]u8 = undefined;
        const path = std.fmt.bufPrint(&buf, "{s}/{s}", .{ project_path, PROJECT_FILE }) catch
            return error.PathTooLong;
        const bytes = std.Io.Dir.cwd().readFileAlloc(io, path, allocator, .limited(256 * 1024)) catch
            return initDefault(allocator, "");
        defer allocator.free(bytes);
        return parse(allocator, bytes);
    }

    /// Serialize back to `project.json` JSON text. Caller owns the result.
    pub fn toJson(self: ProjectConfig, allocator: std.mem.Allocator) ![]u8 {
        var out: std.ArrayList(u8) = .empty;
        errdefer out.deinit(allocator);
        const w = &out;
        try w.appendSlice(allocator, "{\n");
        try appendJsonField(allocator, w, "turian_version", self.turian_version, true);
        try appendJsonField(allocator, w, "name", self.name, false);
        try appendJsonField(allocator, w, "version", self.version, false);
        try w.appendSlice(allocator, ",\n  \"dependencies\": {");
        for (self.dependencies, 0..) |d, i| {
            if (i > 0) try w.appendSlice(allocator, ",");
            try w.appendSlice(allocator, "\n    ");
            try appendJsonString(allocator, w, d.name);
            try w.appendSlice(allocator, ": { ");
            var first = true;
            if (d.url.len > 0) {
                try appendKv(allocator, w, "url", d.url, &first);
            }
            if (d.hash.len > 0) {
                try appendKv(allocator, w, "hash", d.hash, &first);
            }
            if (d.path.len > 0) {
                try appendKv(allocator, w, "path", d.path, &first);
            }
            try w.appendSlice(allocator, " }");
        }
        if (self.dependencies.len > 0) try w.appendSlice(allocator, "\n  ");
        try w.appendSlice(allocator, "},\n  \"packages\": {");
        for (self.packages, 0..) |p, i| {
            if (i > 0) try w.appendSlice(allocator, ",");
            try w.appendSlice(allocator, "\n    ");
            try appendJsonString(allocator, w, p.name);
            try w.appendSlice(allocator, ": { ");
            var first = true;
            try appendKv(allocator, w, "version", p.version, &first);
            if (p.source.len > 0) {
                try appendKv(allocator, w, if (p.is_git) "url" else "path", p.source, &first);
            }
            try w.appendSlice(allocator, " }");
        }
        if (self.packages.len > 0) try w.appendSlice(allocator, "\n  ");
        try w.appendSlice(allocator, "}\n}\n");
        return out.toOwnedSlice(allocator);
    }

    /// Generate the project's `build.zig.zon` from this config. `fallback_name`
    /// is used as the Zig package `.name` when the config has no `name`
    /// (e.g. legacy projects).
    ///
    /// `path_prefix`, when non-empty, is prepended to relative `.path`
    /// dependencies so the generated ZON works from a different working
    /// directory (e.g. the project's `.cache/` build). Pass `""` for the
    /// project-root `build.zig.zon`. Caller owns the result.
    pub fn toBuildZon(
        self: ProjectConfig,
        allocator: std.mem.Allocator,
        fallback_name: []const u8,
        path_prefix: []const u8,
    ) ![]u8 {
        return self.toBuildZonExtra(allocator, fallback_name, path_prefix, &.{});
    }

    /// Like `toBuildZon`, plus `extra_deps` appended to the dependency table —
    /// used to inject the `dvui` entry (C10 pay-for-use) for projects that
    /// reference a `.uidoc` asset, without polluting `self.dependencies`
    /// (which stays the user-authored `project.json` source of truth and
    /// feeds the separate `b.dependency()` no-op resolution loop in
    /// `GameCodegen` — `dvui` is wired specially there instead, with its own
    /// backend option, so it must NOT also appear in that generic loop).
    pub fn toBuildZonExtra(
        self: ProjectConfig,
        allocator: std.mem.Allocator,
        fallback_name: []const u8,
        path_prefix: []const u8,
        extra_deps: []const Dependency,
    ) ![]u8 {
        const raw_name = if (self.name.len > 0) self.name else fallback_name;
        var id_buf: [128]u8 = undefined;
        const id = sanitizeId(raw_name, &id_buf);
        const fingerprint = packageFingerprint(id);
        const version = if (self.version.len > 0) self.version else "0.0.0";

        var out: std.ArrayList(u8) = .empty;
        errdefer out.deinit(allocator);
        const w = &out;

        try w.print(allocator,
            \\.{{
            \\    .name = .{s},
            \\    .version = "{s}",
            \\    .fingerprint = 0x{x:0>16},
            \\    .minimum_zig_version = "{s}",
            \\
        , .{ id, version, fingerprint, MIN_ZIG_VERSION });

        if (self.dependencies.len == 0 and extra_deps.len == 0) {
            try w.appendSlice(allocator, "    .dependencies = .{},\n");
        } else {
            try w.appendSlice(allocator, "    .dependencies = .{\n");
            for (self.dependencies) |d| try writeDepEntry(allocator, w, d, path_prefix);
            for (extra_deps) |d| try writeDepEntry(allocator, w, d, path_prefix);
            try w.appendSlice(allocator, "    },\n");
        }

        try w.appendSlice(allocator,
            \\    .paths = .{
            \\        "build.zig",
            \\        "build.zig.zon",
            \\        "assets",
            \\        "scenes",
            \\        "packages",
            \\    },
            \\}
            \\
        );
        return out.toOwnedSlice(allocator);
    }

    fn writeDepEntry(allocator: std.mem.Allocator, w: *std.ArrayList(u8), d: Dependency, path_prefix: []const u8) !void {
        var dep_id_buf: [128]u8 = undefined;
        const dep_id = sanitizeId(d.name, &dep_id_buf);
        try w.print(allocator, "        .{s} = .{{\n", .{dep_id});
        if (d.path.len > 0) {
            if (path_prefix.len > 0 and !std.fs.path.isAbsolute(d.path)) {
                try w.print(allocator, "            .path = \"{s}/{s}\",\n", .{ path_prefix, d.path });
            } else {
                try w.print(allocator, "            .path = \"{s}\",\n", .{d.path});
            }
        } else {
            if (d.url.len > 0) try w.print(allocator, "            .url = \"{s}\",\n", .{d.url});
            if (d.hash.len > 0) try w.print(allocator, "            .hash = \"{s}\",\n", .{d.hash});
        }
        try w.appendSlice(allocator, "        },\n");
    }

    /// Names of all declared dependencies (for the generated `b.dependency()`
    /// resolution loop in `GameCodegen`). Caller owns the slice (strings are
    /// borrowed from `self`).
    pub fn dependencyNames(self: ProjectConfig, allocator: std.mem.Allocator) ![][]const u8 {
        const out = try allocator.alloc([]const u8, self.dependencies.len);
        for (self.dependencies, 0..) |d, i| out[i] = d.name;
        return out;
    }
};

// ── JSON serialization helpers ──────────────────────────────────────────────

fn appendJsonField(a: std.mem.Allocator, w: *std.ArrayList(u8), key: []const u8, value: []const u8, first: bool) !void {
    try w.appendSlice(a, if (first) "  " else ",\n  ");
    try appendJsonString(a, w, key);
    try w.appendSlice(a, ": ");
    try appendJsonString(a, w, value);
}

fn appendKv(a: std.mem.Allocator, w: *std.ArrayList(u8), key: []const u8, value: []const u8, first: *bool) !void {
    if (!first.*) try w.appendSlice(a, ", ");
    first.* = false;
    try appendJsonString(a, w, key);
    try w.appendSlice(a, ": ");
    try appendJsonString(a, w, value);
}

fn appendJsonString(a: std.mem.Allocator, w: *std.ArrayList(u8), s: []const u8) !void {
    try w.append(a, '"');
    for (s) |c| switch (c) {
        '"' => try w.appendSlice(a, "\\\""),
        '\\' => try w.appendSlice(a, "\\\\"),
        '\n' => try w.appendSlice(a, "\\n"),
        '\r' => try w.appendSlice(a, "\\r"),
        '\t' => try w.appendSlice(a, "\\t"),
        else => try w.append(a, c),
    };
    try w.append(a, '"');
}

// ── JSON parsing helpers ────────────────────────────────────────────────────

fn getStr(obj: std.json.ObjectMap, key: []const u8) ?[]const u8 {
    const v = obj.get(key) orelse return null;
    return if (v == .string) v.string else null;
}

fn dupeStr(a: std.mem.Allocator, s: []const u8) ![]const u8 {
    return a.dupe(u8, s);
}

fn parseDeps(a: std.mem.Allocator, val: ?std.json.Value) ![]Dependency {
    const v = val orelse return a.alloc(Dependency, 0);
    if (v != .object) return a.alloc(Dependency, 0);
    const map = v.object;
    var out = try a.alloc(Dependency, map.count());
    var filled: usize = 0;
    errdefer {
        for (out[0..filled]) |d| {
            a.free(d.name);
            a.free(d.url);
            a.free(d.hash);
            a.free(d.path);
        }
        a.free(out);
    }
    var it = map.iterator();
    while (it.next()) |entry| {
        const name = try a.dupe(u8, entry.key_ptr.*);
        var dep = Dependency{ .name = name };
        if (entry.value_ptr.* == .object) {
            const o = entry.value_ptr.*.object;
            dep.url = try a.dupe(u8, getStr(o, "url") orelse "");
            dep.hash = try a.dupe(u8, getStr(o, "hash") orelse "");
            dep.path = try a.dupe(u8, getStr(o, "path") orelse "");
        } else {
            dep.url = try a.dupe(u8, "");
            dep.hash = try a.dupe(u8, "");
            dep.path = try a.dupe(u8, "");
        }
        out[filled] = dep;
        filled += 1;
    }
    return out;
}

fn freeDeps(a: std.mem.Allocator, deps: []Dependency) void {
    for (deps) |d| {
        a.free(d.name);
        a.free(d.url);
        a.free(d.hash);
        a.free(d.path);
    }
    a.free(deps);
}

fn parsePackages(a: std.mem.Allocator, val: ?std.json.Value) ![]StorePackage {
    const v = val orelse return a.alloc(StorePackage, 0);
    if (v != .object) return a.alloc(StorePackage, 0);
    const map = v.object;
    var out = try a.alloc(StorePackage, map.count());
    var filled: usize = 0;
    errdefer {
        for (out[0..filled]) |p| {
            a.free(p.name);
            a.free(p.version);
            a.free(p.source);
        }
        a.free(out);
    }
    var it = map.iterator();
    while (it.next()) |entry| {
        var p = StorePackage{
            .name = try a.dupe(u8, entry.key_ptr.*),
            .version = try a.dupe(u8, ""),
            .source = try a.dupe(u8, ""),
        };
        if (entry.value_ptr.* == .object) {
            const o = entry.value_ptr.*.object;
            a.free(p.version);
            p.version = try a.dupe(u8, getStr(o, "version") orelse "");
            if (getStr(o, "url")) |u| {
                a.free(p.source);
                p.source = try a.dupe(u8, u);
                p.is_git = true;
            } else if (getStr(o, "path")) |pa| {
                a.free(p.source);
                p.source = try a.dupe(u8, pa);
                p.is_git = false;
            }
        }
        out[filled] = p;
        filled += 1;
    }
    return out;
}

// ── identifier / fingerprint helpers (shared with ProjectOps) ───────────────

/// FNV-1a 64-bit hash of `s`. Deterministic, used to derive the stable random
/// `id` half of a package fingerprint.
pub fn fnv1a64(s: []const u8) u64 {
    var h: u64 = 14695981039346656037;
    for (s) |c| {
        h ^= @as(u64, c);
        h *%= 1099511628211;
    }
    return h;
}

/// Build a valid Zig package fingerprint for a package named `id` (the
/// already-sanitized `.name`). Zig requires `fingerprint = checksum << 32 | id`
/// where `checksum = Crc32(name)` and `id` is a stable, non-degenerate random
/// value (neither 0x00000000 nor 0xffffffff). We derive a deterministic `id`
/// from the name's FNV-1a hash so regeneration is reproducible.
pub fn packageFingerprint(id: []const u8) u64 {
    const checksum: u64 = std.hash.Crc32.hash(id);
    var low: u32 = @truncate(fnv1a64(id));
    if (low == 0x0000_0000 or low == 0xffff_ffff) low = 0x7155_4e01;
    return (checksum << 32) | low;
}

/// Copy `name` into `buf`, replacing non-alphanumeric chars with `_`. Zig
/// package names must be **bare** identifiers (no `.@"..."`, no leading digit),
/// so a leading digit is replaced with `_`. The fingerprint is computed over the
/// final sanitized string, keeping it self-consistent.
pub fn sanitizeId(name: []const u8, buf: []u8) []const u8 {
    const len = @min(name.len, buf.len);
    for (name[0..len], 0..) |c, i| {
        buf[i] = if (std.ascii.isAlphanumeric(c)) c else '_';
    }
    if (len == 0) return "turian_project";
    if (std.ascii.isDigit(buf[0])) buf[0] = '_';
    return buf[0..len];
}

// ── tests ───────────────────────────────────────────────────────────────────

test "parse legacy sentinel" {
    const json = "{\"turian_version\":\"0.16\"}";
    const c = try ProjectConfig.parse(std.testing.allocator, json);
    defer c.deinit();
    try std.testing.expectEqualStrings("0.16", c.turian_version);
    try std.testing.expectEqual(@as(usize, 0), c.dependencies.len);
}

test "parse with dependencies" {
    const json =
        \\{
        \\  "turian_version": "0.16",
        \\  "name": "My Game",
        \\  "version": "1.2.3",
        \\  "dependencies": {
        \\    "foo": { "url": "git+https://x/y#z", "hash": "abc" },
        \\    "bar": { "path": "../bar" }
        \\  }
        \\}
    ;
    const c = try ProjectConfig.parse(std.testing.allocator, json);
    defer c.deinit();
    try std.testing.expectEqualStrings("My Game", c.name);
    try std.testing.expectEqualStrings("1.2.3", c.version);
    try std.testing.expectEqual(@as(usize, 2), c.dependencies.len);
}

test "toBuildZon round-trips name and deps" {
    const json =
        \\{ "name": "My Game", "version": "1.0.0",
        \\  "dependencies": { "foo": { "url": "u", "hash": "h" } } }
    ;
    const c = try ProjectConfig.parse(std.testing.allocator, json);
    defer c.deinit();
    const zon = try c.toBuildZon(std.testing.allocator, "fallback", "");
    defer std.testing.allocator.free(zon);
    try std.testing.expect(std.mem.indexOf(u8, zon, ".name = .My_Game") != null);
    try std.testing.expect(std.mem.indexOf(u8, zon, ".version = \"1.0.0\"") != null);
    try std.testing.expect(std.mem.indexOf(u8, zon, ".foo = .{") != null);
    try std.testing.expect(std.mem.indexOf(u8, zon, ".url = \"u\"") != null);
    try std.testing.expect(std.mem.indexOf(u8, zon, ".hash = \"h\"") != null);
}

test "toBuildZon makes leading-digit names valid bare identifiers" {
    const c = try ProjectConfig.initDefault(std.testing.allocator, "3d-model-materials");
    defer c.deinit();
    const zon = try c.toBuildZon(std.testing.allocator, "fallback", "");
    defer std.testing.allocator.free(zon);
    // Zig package names must be bare identifiers: leading digit → '_'.
    try std.testing.expect(std.mem.indexOf(u8, zon, ".name = ._d_model_materials") != null);
    try std.testing.expect(std.mem.indexOf(u8, zon, ".@\"") == null);
}

test "toBuildZon empty deps uses fallback name" {
    const c = try ProjectConfig.initDefault(std.testing.allocator, "");
    defer c.deinit();
    const zon = try c.toBuildZon(std.testing.allocator, "Cool Project", "");
    defer std.testing.allocator.free(zon);
    try std.testing.expect(std.mem.indexOf(u8, zon, ".name = .Cool_Project") != null);
    try std.testing.expect(std.mem.indexOf(u8, zon, ".dependencies = .{}") != null);
}

test "toBuildZon path_prefix absolutizes relative path deps" {
    const json =
        \\{ "name": "G", "dependencies": { "loc": { "path": "../loc" }, "abs": { "path": "/opt/x" } } }
    ;
    const c = try ProjectConfig.parse(std.testing.allocator, json);
    defer c.deinit();
    const zon = try c.toBuildZon(std.testing.allocator, "G", "/proj");
    defer std.testing.allocator.free(zon);
    try std.testing.expect(std.mem.indexOf(u8, zon, ".path = \"/proj/../loc\"") != null);
    // Absolute path deps are left untouched.
    try std.testing.expect(std.mem.indexOf(u8, zon, ".path = \"/opt/x\"") != null);
}

test "toJson round-trips through parse" {
    const json =
        \\{ "name": "Game", "version": "0.1.0",
        \\  "dependencies": { "dep": { "path": "../p" } } }
    ;
    const c = try ProjectConfig.parse(std.testing.allocator, json);
    defer c.deinit();
    const out = try c.toJson(std.testing.allocator);
    defer std.testing.allocator.free(out);
    const c2 = try ProjectConfig.parse(std.testing.allocator, out);
    defer c2.deinit();
    try std.testing.expectEqualStrings("Game", c2.name);
    try std.testing.expectEqual(@as(usize, 1), c2.dependencies.len);
    try std.testing.expectEqualStrings("../p", c2.dependencies[0].path);
}
