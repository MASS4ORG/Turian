const std = @import("std");
const Guid = @import("guid").Guid;
const AssetType = @import("types/AssetType.zig").AssetType;
const asset_meta = @import("AssetMeta.zig");
const asset_cache = @import("AssetCache.zig");

// ── Types ─────────────────────────────────────────────────────────────────────

/// Information about a single asset in the database.
pub const AssetInfo = struct {
    /// Stable asset GUID.
    guid: Guid,
    /// Owned by the database; valid until the next scan or deinit.
    path: []const u8,
    /// Asset type category.
    asset_type: AssetType,
};

/// Describes how an asset changed during a scan.
pub const ChangeKind = enum { added, removed, modified };

/// A change event produced during asset scanning.
pub const ChangeEvent = struct {
    /// What kind of change occurred.
    kind: ChangeKind,
    /// Which asset changed.
    guid: Guid,
};

// ── Iterator ──────────────────────────────────────────────────────────────────

/// Iterator over assets of a specific type. Obtained via AssetDatabase.enumerate().
pub const Iterator = struct {
    inner: GuidMap.ValueIterator,
    filter: AssetType,

    /// Returns the next matching asset, or null.
    pub fn next(self: *Iterator) ?AssetInfo {
        while (self.inner.next()) |ptr| {
            if (ptr.asset_type == self.filter) return ptr.*;
        }
        return null;
    }
};

// ── Internal map types ────────────────────────────────────────────────────────

const GuidMap = std.AutoHashMap(Guid, AssetInfo);

// ── AssetDatabase ─────────────────────────────────────────────────────────────

/// In-memory index of all assets in a project, keyed by GUID and path.
pub const AssetDatabase = struct {
    allocator: std.mem.Allocator,
    /// Primary index: GUID → AssetInfo (owns the path strings).
    by_guid: GuidMap,
    /// Secondary index: path → GUID. Keys point into by_guid values.
    by_path: std.StringHashMap(Guid),

    /// Create an empty database.
    pub fn init(allocator: std.mem.Allocator) AssetDatabase {
        return .{
            .allocator = allocator,
            .by_guid = GuidMap.init(allocator),
            .by_path = std.StringHashMap(Guid).init(allocator),
        };
    }

    /// Frees all owned data and deinitialises maps.
    pub fn deinit(self: *AssetDatabase) void {
        var it = self.by_guid.valueIterator();
        while (it.next()) |info| self.allocator.free(info.path);
        self.by_guid.deinit();
        self.by_path.deinit();
    }

    fn clearEntries(self: *AssetDatabase) void {
        var it = self.by_guid.valueIterator();
        while (it.next()) |info| self.allocator.free(info.path);
        self.by_guid.clearRetainingCapacity();
        self.by_path.clearRetainingCapacity();
    }

    // ── Asset Discovery ───────────────────────────────────────────────────────

    /// Full rescan of `assets_path`. Ensures every asset has a .meta, then
    /// builds GUID ↔ path ↔ type indices.
    pub fn scan(self: *AssetDatabase, io: std.Io, assets_path: []const u8) void {
        self.clearEntries();
        self.scanDir(io, assets_path);
        // Index any already-generated sub-assets (materials/textures cooked from
        // models) so they resolve before the next import runs.
        const project_path = std.fs.path.dirname(assets_path) orelse ".";
        self.registerDerived(io, project_path);
    }

    /// Index derived sub-assets recorded in source `.meta` manifests as virtual
    /// entries whose path points at their cooked cache artifact. Idempotent —
    /// already-registered GUIDs are skipped — so it is safe to call after both
    /// `scan` and an import pass.
    pub fn registerDerived(self: *AssetDatabase, io: std.Io, project_path: []const u8) void {
        // Snapshot source paths first: inserting may rehash and invalidate the
        // iterator and its value pointers.
        var paths: std.ArrayList([]const u8) = .empty;
        defer {
            for (paths.items) |p| self.allocator.free(p);
            paths.deinit(self.allocator);
        }
        {
            var it = self.by_guid.valueIterator();
            while (it.next()) |info| {
                const dup = self.allocator.dupe(u8, info.path) catch continue;
                paths.append(self.allocator, dup) catch self.allocator.free(dup);
            }
        }

        // Parse metas in a scratch arena — only the GUID/type (values) and the
        // duped artifact path are kept; the rest is freed each iteration.
        var scratch = std.heap.ArenaAllocator.init(self.allocator);
        defer scratch.deinit();

        var art_buf: [1024]u8 = undefined;
        for (paths.items) |path| {
            _ = scratch.reset(.retain_capacity);
            const meta = asset_meta.readMeta(io, scratch.allocator(), path);
            for (meta.sub_assets) |sub| {
                if (sub.guid.isNil() or self.by_guid.contains(sub.guid)) continue;
                const art = asset_cache.artifactPath(project_path, sub.guid, sub.asset_type, &art_buf) orelse continue;
                const owned = self.allocator.dupe(u8, art) catch continue;
                self.by_guid.put(sub.guid, .{
                    .guid = sub.guid,
                    .path = owned,
                    .asset_type = sub.asset_type,
                }) catch {
                    self.allocator.free(owned);
                    continue;
                };
                self.by_path.put(owned, sub.guid) catch {};
            }
        }
    }

    fn scanDir(self: *AssetDatabase, io: std.Io, dir_path: []const u8) void {
        var dir = std.Io.Dir.cwd().openDir(io, dir_path, .{ .iterate = true }) catch return;
        defer dir.close(io);

        var iter = dir.iterate();
        while (iter.next(io) catch null) |entry| {
            if (entry.kind == .directory) {
                var sub_buf: [1024]u8 = undefined;
                const sub = std.fmt.bufPrint(&sub_buf, "{s}/{s}", .{ dir_path, entry.name }) catch continue;
                self.scanDir(io, sub);
            } else if (entry.kind == .file and !std.mem.endsWith(u8, entry.name, ".meta")) {
                var path_buf: [1024]u8 = undefined;
                const asset_path = std.fmt.bufPrint(&path_buf, "{s}/{s}", .{ dir_path, entry.name }) catch continue;
                self.register(io, asset_path);
            }
        }
    }

    fn register(self: *AssetDatabase, io: std.Io, asset_path: []const u8) void {
        const meta = asset_meta.ensureMeta(io, self.allocator, asset_path);
        if (meta.guid.isNil()) return;

        const owned_path = self.allocator.dupe(u8, asset_path) catch return;
        // Free the previous path if this GUID is already indexed (e.g. two assets
        // mistakenly share a GUID) so the old allocation isn't leaked.
        if (self.by_guid.fetchRemove(meta.guid)) |old| {
            _ = self.by_path.remove(old.value.path);
            self.allocator.free(old.value.path);
        }
        self.by_guid.put(meta.guid, .{
            .guid = meta.guid,
            .path = owned_path,
            .asset_type = meta.asset_type,
        }) catch {
            self.allocator.free(owned_path);
            return;
        };
        // Key points into the owned_path stored in by_guid — valid for our lifetime.
        self.by_path.put(owned_path, meta.guid) catch {};
    }

    // ── Query API ─────────────────────────────────────────────────────────────

    pub fn findByGuid(self: *AssetDatabase, guid: Guid) ?AssetInfo {
        return self.by_guid.get(guid);
    }

    pub fn findByPath(self: *AssetDatabase, path: []const u8) ?AssetInfo {
        const guid = self.by_path.get(path) orelse return null;
        return self.by_guid.get(guid);
    }

    pub fn exists(self: *AssetDatabase, guid: Guid) bool {
        return self.by_guid.contains(guid);
    }

    pub fn count(self: *AssetDatabase) usize {
        return self.by_guid.count();
    }

    /// Iterate all assets of a given type. Use as:
    ///   var it = db.enumerate(.image);
    ///   while (it.next()) |info| { ... }
    pub fn enumerate(self: *AssetDatabase, asset_type: AssetType) Iterator {
        return .{
            .inner = self.by_guid.valueIterator(),
            .filter = asset_type,
        };
    }

    // ── Dependency Support ────────────────────────────────────────────────────

    /// Returns source-level dependency GUIDs recorded in the asset's .meta.
    /// The returned slice is owned by the caller's `allocator`.
    pub fn getDependencies(
        self: *AssetDatabase,
        guid: Guid,
        io: std.Io,
        allocator: std.mem.Allocator,
    ) []const Guid {
        const info = self.findByGuid(guid) orelse return &.{};
        const meta = asset_meta.readMeta(io, allocator, info.path);
        if (meta.source_deps.len == 0) return &.{};
        return allocator.dupe(Guid, meta.source_deps) catch &.{};
    }
};
