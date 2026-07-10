const std = @import("std");
const FieldType = @import("FieldType.zig").FieldType;
const MAX_REF_LEN = @import("GameObjectRef.zig").MAX_REF_LEN;

/// Asset type category filter for typed asset references.
/// Tagged `u32` so it can live inside the extern `FieldInfo` reflection struct.
/// New variants may only be appended (C-ABI shared libraries depend on the values).
pub const AssetFilter = enum(u32) { any, mesh, texture, audio, material, input_actions, scene, ui_document, game_event, font };

/// Weak reference to an asset by stable GUID string.
pub const AssetRef = struct {
    buf: [MAX_REF_LEN]u8 = .{0} ** MAX_REF_LEN,
    len: usize = 0,
    pub const _turian_ref_kind: FieldType = .asset_ref;
    pub const _turian_asset_filter: AssetFilter = .any;

    /// Returns the GUID string, or empty if unset.
    pub fn slice(self: *const @This()) []const u8 {
        return self.buf[0..self.len];
    }
    /// Alias for `slice` — reads more naturally at component call sites
    /// (e.g. `self.next_scene.guid()`).
    pub fn guid(self: *const @This()) []const u8 {
        return self.buf[0..self.len];
    }
    /// Sets the GUID string, truncating if necessary.
    pub fn set(self: *@This(), s: []const u8) void {
        const l = @min(s.len, MAX_REF_LEN);
        @memcpy(self.buf[0..l], s[0..l]);
        @memset(self.buf[l..], 0);
        self.len = l;
    }

    /// serde hook: on the wire a ref is its plain GUID string, never the
    /// fixed buf/len internals (assets stay hand-readable JSON).
    pub fn zerdeSerialize(self: @This(), serializer: anytype) @TypeOf(serializer.*).Error!void {
        return serializer.serializeString(self.slice());
    }

    pub fn zerdeDeserialize(comptime T: type, allocator: std.mem.Allocator, deserializer: anytype) @TypeOf(deserializer.*).Error!T {
        const s = try deserializer.deserializeString(allocator);
        defer allocator.free(s);
        var ref: T = .{};
        ref.set(s);
        return ref;
    }
};

/// Returns a type like AssetRef but annotated with a compile-time asset filter.
/// The inspector uses _turian_asset_filter to restrict drag-drop to matching assets.
pub fn TypedAssetRef(comptime filter: AssetFilter) type {
    return struct {
        buf: [MAX_REF_LEN]u8 = .{0} ** MAX_REF_LEN,
        len: usize = 0,
        pub const _turian_ref_kind: FieldType = .asset_ref;
        pub const _turian_asset_filter: AssetFilter = filter;

        /// Returns the GUID string, or empty if unset.
        pub fn slice(self: *const @This()) []const u8 {
            return self.buf[0..self.len];
        }
        /// Alias for `slice` — reads more naturally at component call sites
        /// (e.g. `self.next_scene.guid()`).
        pub fn guid(self: *const @This()) []const u8 {
            return self.buf[0..self.len];
        }
        /// Sets the GUID string, truncating if necessary.
        pub fn set(self: *@This(), s: []const u8) void {
            const l = @min(s.len, MAX_REF_LEN);
            @memcpy(self.buf[0..l], s[0..l]);
            @memset(self.buf[l..], 0);
            self.len = l;
        }

        /// serde hook: on the wire a ref is its plain GUID string, never the
        /// fixed buf/len internals (assets stay hand-readable JSON).
        pub fn zerdeSerialize(self: @This(), serializer: anytype) @TypeOf(serializer.*).Error!void {
            return serializer.serializeString(self.slice());
        }

        pub fn zerdeDeserialize(comptime T: type, allocator: std.mem.Allocator, deserializer: anytype) @TypeOf(deserializer.*).Error!T {
            const s = try deserializer.deserializeString(allocator);
            defer allocator.free(s);
            var ref: T = .{};
            ref.set(s);
            return ref;
        }
    };
}
