//! Compiled `.strtab` binary format: a sorted id array plus a string blob,
//! baked from a `.strings` asset at bake time (`editor/i18n/Compiler.zig`,
//! P2) and packed into `game.oap` (`editor/assets/AssetPackager.zig`).
//!
//! Lookup is a binary search that compares full id bytes on every step —
//! there is no hash column. Binary search already touches the full id at the
//! decision point, so a hash would only add a redundant pre-filter; it would
//! never be load-bearing for correctness. This matters because a silent hash
//! collision in a shipped game's compiled string table is not something a
//! player-side bug report can diagnose.
//!
//! `get()` returns a slice pointing directly into the caller-owned `bytes`
//! backing store — zero allocation, no copy. The reader borrows `bytes` and
//! never frees it; the owning `Locale` service is responsible for keeping
//! loaded blobs alive for the session (see `Locale.zig`).
//!
//! Layout (little-endian):
//! ```
//! Header   { magic: "STRT", version: u32, count: u32, locale_len: u32 }
//! locale   [locale_len]u8                          // BCP-47 tag, not NUL-terminated
//! entries  [count]Entry                             // sorted by id, ascending byte order
//!   Entry  { id_offset: u32, id_len: u32, value_offset: u32, value_len: u32 } // offsets into blob
//! blob     [..]u8                                   // concatenated id/value bytes
//! ```

const std = @import("std");

pub const MAGIC = "STRT";
pub const CURRENT_VERSION: u32 = 1;
const HEADER_LEN = 4 + 4 + 4 + 4;
const ENTRY_LEN = 4 + 4 + 4 + 4;

pub const Error = error{ InvalidMagic, UnsupportedVersion, Truncated };

/// One id -> translated-string pair, as fed to `encode`.
pub const Unit = struct {
    id: []const u8,
    value: []const u8,
};

/// Read-only view over an encoded `.strtab` blob.
pub const StringTable = struct {
    bytes: []const u8,
    locale: []const u8,
    count: usize,
    entries_start: usize,
    blob_start: usize,

    pub fn init(bytes: []const u8) Error!StringTable {
        if (bytes.len < HEADER_LEN) return error.Truncated;
        if (!std.mem.eql(u8, bytes[0..4], MAGIC)) return error.InvalidMagic;
        const version = readU32(bytes, 4);
        if (version != CURRENT_VERSION) return error.UnsupportedVersion;
        const count = readU32(bytes, 8);
        const locale_len = readU32(bytes, 12);

        const locale_start = HEADER_LEN;
        if (bytes.len < locale_start + locale_len) return error.Truncated;
        const entries_start = locale_start + locale_len;
        const entries_len = @as(usize, count) * ENTRY_LEN;
        if (bytes.len < entries_start + entries_len) return error.Truncated;
        const blob_start = entries_start + entries_len;

        return .{
            .bytes = bytes,
            .locale = bytes[locale_start..entries_start],
            .count = count,
            .entries_start = entries_start,
            .blob_start = blob_start,
        };
    }

    const Entry = struct { id_offset: u32, id_len: u32, value_offset: u32, value_len: u32 };

    fn entryAt(self: *const StringTable, i: usize) Entry {
        const off = self.entries_start + i * ENTRY_LEN;
        return .{
            .id_offset = readU32(self.bytes, off),
            .id_len = readU32(self.bytes, off + 4),
            .value_offset = readU32(self.bytes, off + 8),
            .value_len = readU32(self.bytes, off + 12),
        };
    }

    fn blobSlice(self: *const StringTable, offset: u32, len: u32) []const u8 {
        const start = self.blob_start + offset;
        return self.bytes[start .. start + len];
    }

    /// Look up `id`, or null if absent. Zero-allocation: the returned slice
    /// points into `self.bytes`.
    pub fn get(self: *const StringTable, id: []const u8) ?[]const u8 {
        var lo: usize = 0;
        var hi: usize = self.count;
        while (lo < hi) {
            const mid = lo + (hi - lo) / 2;
            const e = self.entryAt(mid);
            const mid_id = self.blobSlice(e.id_offset, e.id_len);
            switch (std.mem.order(u8, mid_id, id)) {
                .lt => lo = mid + 1,
                .gt => hi = mid,
                .eq => return self.blobSlice(e.value_offset, e.value_len),
            }
        }
        return null;
    }
};

fn readU32(bytes: []const u8, offset: usize) u32 {
    return std.mem.readInt(u32, bytes[offset..][0..4], .little);
}

fn writeU32(writer: *std.Io.Writer, v: u32) std.Io.Writer.Error!void {
    var buf: [4]u8 = undefined;
    std.mem.writeInt(u32, &buf, v, .little);
    try writer.writeAll(&buf);
}

/// Encode `units` (need not be pre-sorted; duplicate ids are rejected) into
/// a `.strtab` blob for locale `locale`. Caller owns and frees the result.
pub fn encode(allocator: std.mem.Allocator, locale: []const u8, units: []const Unit) ![]u8 {
    const sorted = try allocator.dupe(Unit, units);
    defer allocator.free(sorted);
    std.mem.sort(Unit, sorted, {}, struct {
        fn lessThan(_: void, a: Unit, b: Unit) bool {
            return std.mem.order(u8, a.id, b.id) == .lt;
        }
    }.lessThan);

    var i: usize = 1;
    while (i < sorted.len) : (i += 1) {
        if (std.mem.eql(u8, sorted[i - 1].id, sorted[i].id)) return error.DuplicateId;
    }

    var out: std.Io.Writer.Allocating = .init(allocator);
    defer out.deinit();
    const w = &out.writer;

    try w.writeAll(MAGIC);
    try writeU32(w, CURRENT_VERSION);
    try writeU32(w, @intCast(sorted.len));
    try writeU32(w, @intCast(locale.len));
    try w.writeAll(locale);

    var blob_offset: u32 = 0;
    for (sorted) |u| {
        try writeU32(w, blob_offset);
        try writeU32(w, @intCast(u.id.len));
        blob_offset += @intCast(u.id.len);
        try writeU32(w, blob_offset);
        try writeU32(w, @intCast(u.value.len));
        blob_offset += @intCast(u.value.len);
    }
    for (sorted) |u| {
        try w.writeAll(u.id);
        try w.writeAll(u.value);
    }

    return out.toOwnedSlice();
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

const testing = std.testing;

test "encode/decode round-trip, out-of-order input" {
    const units = [_]Unit{
        .{ .id = "menu.file.open", .value = "Open" },
        .{ .id = "menu.file.close", .value = "Close" },
        .{ .id = "menu.file.new", .value = "New" },
    };
    const bytes = try encode(testing.allocator, "en", &units);
    defer testing.allocator.free(bytes);

    const table = try StringTable.init(bytes);
    try testing.expectEqualStrings("en", table.locale);
    try testing.expectEqual(@as(usize, 3), table.count);
    try testing.expectEqualStrings("Open", table.get("menu.file.open").?);
    try testing.expectEqualStrings("Close", table.get("menu.file.close").?);
    try testing.expectEqualStrings("New", table.get("menu.file.new").?);
    try testing.expectEqual(@as(?[]const u8, null), table.get("menu.file.missing"));
}

test "golden bytes for a known single-entry table" {
    const units = [_]Unit{.{ .id = "a", .value = "b" }};
    const bytes = try encode(testing.allocator, "en", &units);
    defer testing.allocator.free(bytes);
    // magic(4) version(4) count(4) locale_len(4) locale(2) entry(16) blob("ab")
    try testing.expectEqual(@as(usize, 4 + 4 + 4 + 4 + 2 + 16 + 2), bytes.len);
    try testing.expectEqualStrings("STRT", bytes[0..4]);
}

test "rejects duplicate ids" {
    const units = [_]Unit{
        .{ .id = "dup", .value = "1" },
        .{ .id = "dup", .value = "2" },
    };
    try testing.expectError(error.DuplicateId, encode(testing.allocator, "en", &units));
}

test "rejects bad magic and truncated buffers" {
    try testing.expectError(error.InvalidMagic, StringTable.init("XXXX" ++ ([_]u8{0} ** 12)));
    try testing.expectError(error.Truncated, StringTable.init("STR"));
}

test "empty table" {
    const bytes = try encode(testing.allocator, "en", &.{});
    defer testing.allocator.free(bytes);
    const table = try StringTable.init(bytes);
    try testing.expectEqual(@as(usize, 0), table.count);
    try testing.expectEqual(@as(?[]const u8, null), table.get("anything"));
}
