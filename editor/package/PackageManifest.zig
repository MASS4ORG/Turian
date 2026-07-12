/// Parsed representation of a `turian-package.json` manifest.
/// See docs/decisions/0001-package-system.md for the full schema spec.
const std = @import("std");

pub const PackageType = enum { asset, source, native, plugin };

pub const ModuleEntry = struct {
    name: []const u8,
    root: []const u8,
};

pub const NativeEntry = struct {
    name: []const u8,
    kind: []const u8,
    lib: []const u8,
    include: []const u8,
};

pub const PluginEntry = struct {
    register: []const u8,
    entry: []const u8,
};

/// A declared dependency edge: another package this one depends on, with a
/// semver range. Authoritative only for asset-only packages; informational for
/// source/native packages (whose `build.zig.zon` is authoritative). See ADR-0001.
pub const DependencyEntry = struct {
    name: []const u8,
    range: []const u8,
};

pub const Diagnostic = struct {
    message: []const u8,
    is_error: bool,
};

pub const ParseError = error{ MissingField, InvalidField, OutOfMemory };

/// Parsed and validated `turian-package.json` manifest.
/// All strings are owned; call `deinit` to free them.
pub const PackageManifest = struct {
    allocator: std.mem.Allocator,

    name: []const u8,
    version: []const u8,
    author: []const u8,
    description: []const u8,
    license: []const u8,
    engine_compat: []const u8,
    types: []PackageType,
    asset_dirs: [][]const u8,
    modules: []ModuleEntry,
    native: []NativeEntry,
    plugin: ?PluginEntry,
    dependencies: []DependencyEntry,

    pub fn deinit(self: PackageManifest) void {
        const a = self.allocator;
        a.free(self.name);
        a.free(self.version);
        a.free(self.author);
        a.free(self.description);
        a.free(self.license);
        a.free(self.engine_compat);
        a.free(self.types);
        for (self.asset_dirs) |d| a.free(d);
        a.free(self.asset_dirs);
        for (self.modules) |m| {
            a.free(m.name);
            a.free(m.root);
        }
        a.free(self.modules);
        for (self.native) |n| {
            a.free(n.name);
            a.free(n.kind);
            a.free(n.lib);
            a.free(n.include);
        }
        a.free(self.native);
        if (self.plugin) |p| {
            a.free(p.register);
            a.free(p.entry);
        }
        for (self.dependencies) |d| {
            a.free(d.name);
            a.free(d.range);
        }
        a.free(self.dependencies);
    }

    /// Parse and validate `turian-package.json` bytes.
    /// Returns `ParseError.MissingField` when required fields are absent.
    pub fn parse(allocator: std.mem.Allocator, json_bytes: []const u8) ParseError!PackageManifest {
        const parsed = std.json.parseFromSlice(std.json.Value, allocator, json_bytes, .{}) catch
            return ParseError.InvalidField;
        defer parsed.deinit();

        const obj = if (parsed.value == .object) parsed.value.object else return ParseError.InvalidField;

        const name = try dupeStr(allocator, getStr(obj, "name") orelse return ParseError.MissingField);
        errdefer allocator.free(name);
        if (name.len == 0) return ParseError.MissingField;

        const version = try dupeStr(allocator, getStr(obj, "version") orelse return ParseError.MissingField);
        errdefer allocator.free(version);

        const author = try dupeStr(allocator, getStr(obj, "author") orelse "");
        errdefer allocator.free(author);

        const description = try dupeStr(allocator, getStr(obj, "description") orelse "");
        errdefer allocator.free(description);

        const license = try dupeStr(allocator, getStr(obj, "license") orelse "");
        errdefer allocator.free(license);

        const engine_compat = try dupeStr(allocator, getStr(obj, "engine_compat") orelse "");
        errdefer allocator.free(engine_compat);

        // types — required, non-empty array of strings
        const types = try parseTypes(allocator, obj.get("types") orelse return ParseError.MissingField);
        errdefer allocator.free(types);

        // assets — optional array of strings, default ["assets"]
        const asset_dirs = try parseStringArray(allocator, obj.get("assets"), "assets");
        errdefer {
            for (asset_dirs) |d| allocator.free(d);
            allocator.free(asset_dirs);
        }

        // modules — optional array of {name, root}
        const modules = try parseModules(allocator, obj.get("modules"));
        errdefer {
            for (modules) |m| {
                allocator.free(m.name);
                allocator.free(m.root);
            }
            allocator.free(modules);
        }

        // native — optional array of {name, kind, lib, include}
        const native = try parseNative(allocator, obj.get("native"));
        errdefer {
            for (native) |n| {
                allocator.free(n.name);
                allocator.free(n.kind);
                allocator.free(n.lib);
                allocator.free(n.include);
            }
            allocator.free(native);
        }

        // plugin — optional object
        const plugin = try parsePlugin(allocator, obj.get("plugin"));
        errdefer if (plugin) |p| {
            allocator.free(p.register);
            allocator.free(p.entry);
        };

        // dependencies — optional object { "<pkg-name>": "<semver-range>" }
        const dependencies = try parseDependencies(allocator, obj.get("dependencies"));
        errdefer {
            for (dependencies) |d| {
                allocator.free(d.name);
                allocator.free(d.range);
            }
            allocator.free(dependencies);
        }

        return PackageManifest{
            .allocator = allocator,
            .name = name,
            .version = version,
            .author = author,
            .description = description,
            .license = license,
            .engine_compat = engine_compat,
            .types = types,
            .asset_dirs = asset_dirs,
            .modules = modules,
            .native = native,
            .plugin = plugin,
            .dependencies = dependencies,
        };
    }

    pub fn hasType(self: PackageManifest, t: PackageType) bool {
        for (self.types) |pt| if (pt == t) return true;
        return false;
    }
};

// ── helpers ───────────────────────────────────────────────────────────────────

fn getStr(obj: std.json.ObjectMap, key: []const u8) ?[]const u8 {
    const v = obj.get(key) orelse return null;
    return if (v == .string) v.string else null;
}

fn dupeStr(allocator: std.mem.Allocator, s: []const u8) ParseError![]const u8 {
    return allocator.dupe(u8, s) catch ParseError.OutOfMemory;
}

fn parseTypes(allocator: std.mem.Allocator, val: std.json.Value) ParseError![]PackageType {
    const arr = if (val == .array) val.array else return ParseError.InvalidField;
    if (arr.items.len == 0) return ParseError.MissingField;
    const out = allocator.alloc(PackageType, arr.items.len) catch return ParseError.OutOfMemory;
    for (arr.items, 0..) |item, i| {
        const s = if (item == .string) item.string else return ParseError.InvalidField;
        out[i] = std.meta.stringToEnum(PackageType, s) orelse return ParseError.InvalidField;
    }
    return out;
}

fn parseStringArray(
    allocator: std.mem.Allocator,
    val: ?std.json.Value,
    default: []const u8,
) ParseError![][]const u8 {
    if (val == null or val.? == .null) {
        const out = allocator.alloc([]const u8, 1) catch return ParseError.OutOfMemory;
        out[0] = allocator.dupe(u8, default) catch return ParseError.OutOfMemory;
        return out;
    }
    const arr = if (val.? == .array) val.?.array else return ParseError.InvalidField;
    const out = allocator.alloc([]const u8, arr.items.len) catch return ParseError.OutOfMemory;
    var filled: usize = 0;
    errdefer {
        for (out[0..filled]) |s| allocator.free(s);
        allocator.free(out);
    }
    for (arr.items, 0..) |item, i| {
        const s = if (item == .string) item.string else return ParseError.InvalidField;
        out[i] = allocator.dupe(u8, s) catch return ParseError.OutOfMemory;
        filled = i + 1;
    }
    return out;
}

fn parseModules(allocator: std.mem.Allocator, val: ?std.json.Value) ParseError![]ModuleEntry {
    const v = val orelse return allocator.alloc(ModuleEntry, 0) catch ParseError.OutOfMemory;
    if (v == .null) return allocator.alloc(ModuleEntry, 0) catch ParseError.OutOfMemory;
    const arr = if (v == .array) v.array else return ParseError.InvalidField;
    const out = allocator.alloc(ModuleEntry, arr.items.len) catch return ParseError.OutOfMemory;
    var filled: usize = 0;
    errdefer {
        for (out[0..filled]) |m| {
            allocator.free(m.name);
            allocator.free(m.root);
        }
        allocator.free(out);
    }
    for (arr.items, 0..) |item, i| {
        const o = if (item == .object) item.object else return ParseError.InvalidField;
        out[i] = .{
            .name = try dupeStr(allocator, getStr(o, "name") orelse return ParseError.MissingField),
            .root = try dupeStr(allocator, getStr(o, "root") orelse return ParseError.MissingField),
        };
        filled = i + 1;
    }
    return out;
}

fn parseNative(allocator: std.mem.Allocator, val: ?std.json.Value) ParseError![]NativeEntry {
    const v = val orelse return allocator.alloc(NativeEntry, 0) catch ParseError.OutOfMemory;
    if (v == .null) return allocator.alloc(NativeEntry, 0) catch ParseError.OutOfMemory;
    const arr = if (v == .array) v.array else return ParseError.InvalidField;
    const out = allocator.alloc(NativeEntry, arr.items.len) catch return ParseError.OutOfMemory;
    var filled: usize = 0;
    errdefer {
        for (out[0..filled]) |n| {
            allocator.free(n.name);
            allocator.free(n.kind);
            allocator.free(n.lib);
            allocator.free(n.include);
        }
        allocator.free(out);
    }
    for (arr.items, 0..) |item, i| {
        const o = if (item == .object) item.object else return ParseError.InvalidField;
        out[i] = .{
            .name = try dupeStr(allocator, getStr(o, "name") orelse return ParseError.MissingField),
            .kind = try dupeStr(allocator, getStr(o, "kind") orelse "static"),
            .lib = try dupeStr(allocator, getStr(o, "lib") orelse return ParseError.MissingField),
            .include = try dupeStr(allocator, getStr(o, "include") orelse ""),
        };
        filled = i + 1;
    }
    return out;
}

fn parseDependencies(allocator: std.mem.Allocator, val: ?std.json.Value) ParseError![]DependencyEntry {
    const v = val orelse return allocator.alloc(DependencyEntry, 0) catch ParseError.OutOfMemory;
    if (v == .null) return allocator.alloc(DependencyEntry, 0) catch ParseError.OutOfMemory;
    const obj = if (v == .object) v.object else return ParseError.InvalidField;
    const out = allocator.alloc(DependencyEntry, obj.count()) catch return ParseError.OutOfMemory;
    var filled: usize = 0;
    errdefer {
        for (out[0..filled]) |d| {
            allocator.free(d.name);
            allocator.free(d.range);
        }
        allocator.free(out);
    }
    var it = obj.iterator();
    while (it.next()) |entry| {
        const range = if (entry.value_ptr.* == .string) entry.value_ptr.*.string else "";
        out[filled] = .{
            .name = try dupeStr(allocator, entry.key_ptr.*),
            .range = try dupeStr(allocator, range),
        };
        filled += 1;
    }
    return out;
}

fn parsePlugin(allocator: std.mem.Allocator, val: ?std.json.Value) ParseError!?PluginEntry {
    const v = val orelse return null;
    if (v == .null) return null;
    const o = if (v == .object) v.object else return ParseError.InvalidField;
    return PluginEntry{
        .register = try dupeStr(allocator, getStr(o, "register") orelse return ParseError.MissingField),
        .entry = try dupeStr(allocator, getStr(o, "entry") orelse return ParseError.MissingField),
    };
}

// ── tests ─────────────────────────────────────────────────────────────────────

test "parse minimal asset-only manifest" {
    const json =
        \\{
        \\  "name": "com.example.color-palette",
        \\  "version": "1.0.0",
        \\  "types": ["asset"],
        \\  "assets": ["assets"]
        \\}
    ;
    const m = try PackageManifest.parse(std.testing.allocator, json);
    defer m.deinit();
    try std.testing.expectEqualStrings("com.example.color-palette", m.name);
    try std.testing.expectEqualStrings("1.0.0", m.version);
    try std.testing.expect(m.hasType(.asset));
    try std.testing.expectEqual(@as(usize, 1), m.asset_dirs.len);
    try std.testing.expectEqualStrings("assets", m.asset_dirs[0]);
}

test "parse full hybrid manifest" {
    const json =
        \\{
        \\  "name": "com.acme.kit",
        \\  "version": "2.1.0",
        \\  "author": "Acme",
        \\  "license": "MIT",
        \\  "engine_compat": ">=1.0.0",
        \\  "types": ["asset", "source"],
        \\  "assets": ["assets", "extra"],
        \\  "modules": [{"name": "kit", "root": "src/root.zig"}],
        \\  "plugin": {"register": "kit", "entry": "register"}
        \\}
    ;
    const m = try PackageManifest.parse(std.testing.allocator, json);
    defer m.deinit();
    try std.testing.expect(m.hasType(.asset));
    try std.testing.expect(m.hasType(.source));
    try std.testing.expectEqual(@as(usize, 2), m.asset_dirs.len);
    try std.testing.expectEqual(@as(usize, 1), m.modules.len);
    try std.testing.expectEqualStrings("kit", m.modules[0].name);
    try std.testing.expect(m.plugin != null);
}

test "parse manifest dependencies" {
    const json =
        \\{
        \\  "name": "com.acme.kit",
        \\  "version": "1.0.0",
        \\  "types": ["asset"],
        \\  "dependencies": { "com.acme.core": "^1.0.0", "com.acme.ui": ">=2.0.0" }
        \\}
    ;
    const m = try PackageManifest.parse(std.testing.allocator, json);
    defer m.deinit();
    try std.testing.expectEqual(@as(usize, 2), m.dependencies.len);
    // Order follows JSON object iteration; just assert both are present.
    var saw_core = false;
    var saw_ui = false;
    for (m.dependencies) |d| {
        if (std.mem.eql(u8, d.name, "com.acme.core")) saw_core = true;
        if (std.mem.eql(u8, d.name, "com.acme.ui")) saw_ui = true;
    }
    try std.testing.expect(saw_core and saw_ui);
}

test "missing required name returns error" {
    const json =
        \\{ "version": "1.0.0", "types": ["asset"] }
    ;
    try std.testing.expectError(ParseError.MissingField, PackageManifest.parse(std.testing.allocator, json));
}

test "default asset dir when assets field absent" {
    const json =
        \\{ "name": "pkg", "version": "1.0.0", "types": ["asset"] }
    ;
    const m = try PackageManifest.parse(std.testing.allocator, json);
    defer m.deinit();
    try std.testing.expectEqual(@as(usize, 1), m.asset_dirs.len);
    try std.testing.expectEqualStrings("assets", m.asset_dirs[0]);
}
