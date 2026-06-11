//! Generic asset provider interface.
//!
//! Assets are addressed by a string `key` — a virtual path such as
//! "textures/hero.png". A provider supplies the raw bytes of an asset from some
//! backing store: loose files on disk during development, one or more `.oap`
//! packages in release builds, an embedded blob, a network cache, etc.
//!
//! The engine consumes assets only through this interface, so providers are
//! swappable without changing engine code. Compose several with `AssetServer`.

const std = @import("std");

/// Returned by `read` when a provider has no asset for the given key. Callers
/// (e.g. `AssetServer`) treat this as "try the next provider"; any other error
/// is propagated.
pub const Error = error{AssetNotFound};

pub const Provider = struct {
    ptr: *anyopaque,
    vtable: *const VTable,

    pub const VTable = struct {
        /// Read the full bytes of `key`. Returns `error.AssetNotFound` if this
        /// provider cannot supply it. The caller owns the returned slice.
        read: *const fn (
            ptr: *anyopaque,
            gpa: std.mem.Allocator,
            io: std.Io,
            key: []const u8,
        ) anyerror![]u8,
    };

    pub fn read(
        self: Provider,
        gpa: std.mem.Allocator,
        io: std.Io,
        key: []const u8,
    ) anyerror![]u8 {
        return self.vtable.read(self.ptr, gpa, io, key);
    }
};
