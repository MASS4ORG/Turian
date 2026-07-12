const std = @import("std");

// ── Constants ─────────────────────────────────────────────────────────────────

pub const SCHEMA_VERSION: u32 = 1;
/// Directory created inside the home dir (global) or project dir (project).
pub const SETTINGS_DIR = ".turian";
pub const SETTINGS_FILE = "settings.json";

const SCHEMA_KEY = "schema_version";

// ── Subscriber ────────────────────────────────────────────────────────────────

/// Callback invoked when a setting changes.
/// `key` is the changed key. `ctx` is the pointer registered with `subscribe`.
pub const SubscriberFn = *const fn (key: []const u8, ctx: ?*anyopaque) void;

const Subscriber = struct {
    key_prefix: []const u8,
    callback: SubscriberFn,
    ctx: ?*anyopaque,
};

// ── Settings ──────────────────────────────────────────────────────────────────

/// Two-layer cascading settings store backed by JSON files.
///
/// Layer priority (highest first):
///   1. Project layer: `<project_dir>/.turian/settings.json`
///   2. Global layer:  `<global_dir>/.turian/settings.json`
///
/// All reads return the highest-priority value found.
/// All writes go to the project layer when a project path is set, otherwise global.
///
/// In-memory values are stored as raw JSON-encoded strings (e.g. `true`, `42`,
/// `"hello"`, `[1,2,3]`). Typed accessors parse on demand.
///
/// Note: Not thread-safe. Designed for single-threaded editor main-loop access.
pub const Settings = struct {
    allocator: std.mem.Allocator,
    global_path: []u8,
    project_path: ?[]u8,

    global_map: std.StringHashMap([]const u8),
    project_map: std.StringHashMap([]const u8),

    subscribers: std.ArrayList(Subscriber),
    dirty_global: bool,
    dirty_project: bool,
    /// Auto-save interval in milliseconds. 0 = disabled. Caller drives the tick.
    auto_save_interval_ms: u32,

    // ── Init / deinit ─────────────────────────────────────────────────────────

    /// Create a Settings instance. Call `load()` to populate from disk.
    /// `global_dir` is typically the user home directory.
    /// `project_dir` is the open project root, or null for global-only mode.
    pub fn init(
        allocator: std.mem.Allocator,
        global_dir: []const u8,
        project_dir: ?[]const u8,
    ) !Settings {
        const sep = std.fs.path.sep_str;
        const global_path = try std.fmt.allocPrint(
            allocator,
            "{s}" ++ sep ++ SETTINGS_DIR ++ sep ++ SETTINGS_FILE,
            .{global_dir},
        );
        errdefer allocator.free(global_path);

        const project_path: ?[]u8 = if (project_dir) |pd|
            try std.fmt.allocPrint(
                allocator,
                "{s}" ++ sep ++ SETTINGS_DIR ++ sep ++ SETTINGS_FILE,
                .{pd},
            )
        else
            null;

        return .{
            .allocator = allocator,
            .global_path = global_path,
            .project_path = project_path,
            .global_map = std.StringHashMap([]const u8).init(allocator),
            .project_map = std.StringHashMap([]const u8).init(allocator),
            .subscribers = .empty,
            .dirty_global = false,
            .dirty_project = false,
            .auto_save_interval_ms = 0,
        };
    }

    pub fn deinit(self: *Settings) void {
        freeMapEntries(self.allocator, &self.global_map);
        self.global_map.deinit();
        freeMapEntries(self.allocator, &self.project_map);
        self.project_map.deinit();
        self.subscribers.deinit(self.allocator);
        self.allocator.free(self.global_path);
        if (self.project_path) |p| self.allocator.free(p);
    }

    // ── Load / Save ───────────────────────────────────────────────────────────

    /// Load both layers from disk. Safe to call multiple times (reloads).
    /// Missing files are silently ignored — the layer starts empty.
    pub fn load(self: *Settings, io: std.Io) void {
        freeMapEntries(self.allocator, &self.global_map);
        self.global_map.clearRetainingCapacity();
        loadLayer(io, self.allocator, &self.global_map, self.global_path);

        freeMapEntries(self.allocator, &self.project_map);
        self.project_map.clearRetainingCapacity();
        if (self.project_path) |p| loadLayer(io, self.allocator, &self.project_map, p);

        self.dirty_global = false;
        self.dirty_project = false;
    }

    /// Persist dirty layers to disk. Call on editor exit or on each auto-save tick.
    pub fn save(self: *Settings, io: std.Io) void {
        if (self.dirty_global) {
            saveLayer(io, self.allocator, &self.global_map, self.global_path);
            self.dirty_global = false;
        }
        if (self.dirty_project) {
            if (self.project_path) |p| {
                saveLayer(io, self.allocator, &self.project_map, p);
                self.dirty_project = false;
            }
        }
    }

    /// Switch the project layer to a new directory (or null to close the project).
    pub fn setProjectDir(self: *Settings, io: std.Io, project_dir: ?[]const u8) !void {
        const sep = std.fs.path.sep_str;
        const new_path: ?[]u8 = if (project_dir) |pd|
            try std.fmt.allocPrint(
                self.allocator,
                "{s}" ++ sep ++ SETTINGS_DIR ++ sep ++ SETTINGS_FILE,
                .{pd},
            )
        else
            null;

        if (self.project_path) |p| self.allocator.free(p);
        self.project_path = new_path;
        freeMapEntries(self.allocator, &self.project_map);
        self.project_map.clearRetainingCapacity();
        if (new_path) |p| loadLayer(io, self.allocator, &self.project_map, p);
        self.dirty_project = false;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    pub fn getBool(self: *const Settings, key: []const u8, default: bool) bool {
        const raw = self.getNoLock(key) orelse return default;
        if (std.mem.eql(u8, raw, "true")) return true;
        if (std.mem.eql(u8, raw, "false")) return false;
        return default;
    }

    pub fn getInt(self: *const Settings, key: []const u8, default: i64) i64 {
        const raw = self.getNoLock(key) orelse return default;
        return std.fmt.parseInt(i64, raw, 10) catch default;
    }

    pub fn getFloat(self: *const Settings, key: []const u8, default: f64) f64 {
        const raw = self.getNoLock(key) orelse return default;
        return std.fmt.parseFloat(f64, raw) catch default;
    }

    /// Returns the unquoted string value for `key`, or `default`.
    /// The returned slice is valid until the next `set*`, `load`, or `deinit`.
    /// Only works for strings without JSON escape sequences (covers most editor settings).
    pub fn getString(self: *const Settings, key: []const u8, default: []const u8) []const u8 {
        const raw = self.getNoLock(key) orelse return default;
        if (raw.len >= 2 and raw[0] == '"' and raw[raw.len - 1] == '"') {
            const inner = raw[1 .. raw.len - 1];
            if (std.mem.indexOfScalar(u8, inner, '\\') == null) return inner;
        }
        return default;
    }

    /// Returns the raw JSON-encoded value for `key`, or null.
    /// Project layer overrides global layer.
    /// The returned slice is valid until the next `set*`, `load`, or `deinit`.
    pub fn get(self: *const Settings, key: []const u8) ?[]const u8 {
        return self.getNoLock(key);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    pub fn setBool(self: *Settings, key: []const u8, value: bool) !void {
        try self.setRaw(key, if (value) "true" else "false");
        self.notifySubscribers(key);
    }

    pub fn setInt(self: *Settings, key: []const u8, value: i64) !void {
        var buf: [32]u8 = undefined;
        const s = try std.fmt.bufPrint(&buf, "{d}", .{value});
        try self.setRaw(key, s);
        self.notifySubscribers(key);
    }

    pub fn setFloat(self: *Settings, key: []const u8, value: f64) !void {
        var buf: [64]u8 = undefined;
        const s = try std.fmt.bufPrint(&buf, "{}", .{value});
        try self.setRaw(key, s);
        self.notifySubscribers(key);
    }

    pub fn setString(self: *Settings, key: []const u8, value: []const u8) !void {
        var buf: std.ArrayList(u8) = .empty;
        defer buf.deinit(self.allocator);
        try buf.append(self.allocator, '"');
        for (value) |c| switch (c) {
            '"' => try buf.appendSlice(self.allocator, "\\\""),
            '\\' => try buf.appendSlice(self.allocator, "\\\\"),
            '\n' => try buf.appendSlice(self.allocator, "\\n"),
            '\r' => try buf.appendSlice(self.allocator, "\\r"),
            '\t' => try buf.appendSlice(self.allocator, "\\t"),
            else => try buf.append(self.allocator, c),
        };
        try buf.append(self.allocator, '"');
        try self.setRaw(key, buf.items);
        self.notifySubscribers(key);
    }

    /// Write a raw JSON-encoded value. Use typed setters unless you need arrays/objects.
    /// Example: `try settings.set("window.layout", "[1024, 768]");`
    pub fn set(self: *Settings, key: []const u8, json_val: []const u8) !void {
        try self.setRaw(key, json_val);
        self.notifySubscribers(key);
    }

    // ── Subscriptions ─────────────────────────────────────────────────────────

    /// Register a callback for changes to any key with the given prefix.
    /// Use `key_prefix = ""` to receive all changes.
    pub fn subscribe(
        self: *Settings,
        key_prefix: []const u8,
        callback: SubscriberFn,
        ctx: ?*anyopaque,
    ) !void {
        try self.subscribers.append(self.allocator, .{
            .key_prefix = key_prefix,
            .callback = callback,
            .ctx = ctx,
        });
    }

    /// Remove all registrations for `callback`.
    pub fn unsubscribe(self: *Settings, callback: SubscriberFn) void {
        var i: usize = 0;
        while (i < self.subscribers.items.len) {
            if (self.subscribers.items[i].callback == callback) {
                _ = self.subscribers.swapRemove(i);
            } else {
                i += 1;
            }
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    fn getNoLock(self: *const Settings, key: []const u8) ?[]const u8 {
        return self.project_map.get(key) orelse self.global_map.get(key);
    }

    fn setRaw(self: *Settings, key: []const u8, json_val: []const u8) !void {
        const target = if (self.project_path != null) &self.project_map else &self.global_map;

        if (target.getPtr(key)) |val_ptr| {
            const new_val = try self.allocator.dupe(u8, json_val);
            self.allocator.free(val_ptr.*);
            val_ptr.* = new_val;
        } else {
            const owned_key = try self.allocator.dupe(u8, key);
            errdefer self.allocator.free(owned_key);
            const owned_val = try self.allocator.dupe(u8, json_val);
            errdefer self.allocator.free(owned_val);
            try target.put(owned_key, owned_val);
        }

        if (self.project_path != null) {
            self.dirty_project = true;
        } else {
            self.dirty_global = true;
        }
    }

    fn notifySubscribers(self: *Settings, key: []const u8) void {
        for (self.subscribers.items) |sub| {
            if (sub.key_prefix.len == 0 or std.mem.startsWith(u8, key, sub.key_prefix)) {
                sub.callback(key, sub.ctx);
            }
        }
    }
};

// ── File I/O helpers ──────────────────────────────────────────────────────────

fn loadLayer(
    io: std.Io,
    allocator: std.mem.Allocator,
    map: *std.StringHashMap([]const u8),
    path: []const u8,
) void {
    var file = std.Io.Dir.cwd().openFile(io, path, .{}) catch return;
    defer file.close(io);

    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = reader.interface.allocRemaining(allocator, .unlimited) catch return;
    defer allocator.free(content);

    const parsed = std.json.parseFromSlice(
        std.json.Value,
        allocator,
        content,
        .{ .ignore_unknown_fields = true },
    ) catch return;
    defer parsed.deinit();

    if (parsed.value != .object) return;

    var it = parsed.value.object.iterator();
    while (it.next()) |entry| {
        const k = entry.key_ptr.*;
        if (std.mem.eql(u8, k, SCHEMA_KEY)) continue;

        const owned_key = allocator.dupe(u8, k) catch continue;
        const owned_val = valueToJsonAlloc(allocator, entry.value_ptr.*) catch {
            allocator.free(owned_key);
            continue;
        };
        map.put(owned_key, owned_val) catch {
            allocator.free(owned_key);
            allocator.free(owned_val);
        };
    }
}

fn saveLayer(
    io: std.Io,
    allocator: std.mem.Allocator,
    map: *const std.StringHashMap([]const u8),
    path: []const u8,
) void {
    ensureParentDir(io, path);

    var out: std.ArrayList(u8) = .empty;
    defer out.deinit(allocator);

    out.appendSlice(allocator, "{\n  \"" ++ SCHEMA_KEY ++ "\": ") catch return;
    var schema_buf: [8]u8 = undefined;
    const schema_str = std.fmt.bufPrint(&schema_buf, "{d}", .{SCHEMA_VERSION}) catch return;
    out.appendSlice(allocator, schema_str) catch return;

    var it = map.iterator();
    while (it.next()) |entry| {
        out.appendSlice(allocator, ",\n  \"") catch return;
        out.appendSlice(allocator, entry.key_ptr.*) catch return;
        out.appendSlice(allocator, "\": ") catch return;
        out.appendSlice(allocator, entry.value_ptr.*) catch return;
    }

    out.appendSlice(allocator, "\n}\n") catch return;

    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = out.items }) catch {};
}

fn ensureParentDir(io: std.Io, path: []const u8) void {
    const dir = std.fs.path.dirname(path) orelse return;
    std.Io.Dir.cwd().createDirPath(io, dir) catch {};
}

fn freeMapEntries(allocator: std.mem.Allocator, map: *std.StringHashMap([]const u8)) void {
    var it = map.iterator();
    while (it.next()) |entry| {
        allocator.free(entry.key_ptr.*);
        allocator.free(entry.value_ptr.*);
    }
}

/// Serialise a parsed JSON value back to a raw JSON string (heap-allocated).
fn valueToJsonAlloc(allocator: std.mem.Allocator, value: std.json.Value) ![]u8 {
    var out: std.Io.Writer.Allocating = .init(allocator);
    defer out.deinit();
    var stringify: std.json.Stringify = .{ .writer = &out.writer, .options = .{} };
    try stringify.write(value);
    return allocator.dupe(u8, out.written());
}
